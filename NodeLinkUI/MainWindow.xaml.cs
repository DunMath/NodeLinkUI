// MainWindow.xaml.cs  — LITE + SoftThreads wiring + Rolling master.log + Open Logs menu
// Keeps: StartAsMaster from settings, WhoIsAlive, AgentConfig/AgentPulse, UpdateAgentConfig
// Adds: BroadcastPing, ComputeTestResult parser (✓ / ✗ and OK/FAIL), rolling master.log, Open Logs handler
// Fixes: CPU usage probe (uses PerformanceCounter), presence smoothing, no zero flicker on pulse
//
// v1.0.1 changes:
// - [B] Auto-register on discovery: call MasterBootstrapper.OnAgentDiscovered(row) after adding/updating an agent.
// - [C] Register button uses HTTP RegistrationClient.TryRegisterAsync(...) instead of UDP broadcast.
// - [D] Minimal config access via NodeLinkConfig.Load() to get ControlPort/AuthToken defaults.

using NodeComm;
using NodeCore;
using NodeCore.Scheduling;
using NodeMaster;
using NodeAgent;
using NodeAgent.Bootstrap;  // AgentBootstrapper.Start(...)


using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

using Makaretu.Dns;

// v1.0.1: new usings for control plane integration
using NodeCore.Config;              // NodeLinkConfig.Load()
using NodeMaster.Bootstrap;         // MasterBootstrapper.Initialize/OnAgentDiscovered
using MasterApp;                    // RegistrationClient
using NodeCore.Protocol;            // Proto constants (Version/DefaultControlPort)

namespace NodeLinkUI
{
    public partial class MainWindow : Window
    {
        private const string ServiceTypeMaster = "_nodelink._tcp";
        private const string ServiceTypeAgent = "_nodelink-agent._tcp";
        private const string configPath = "node.config";
        private const string settingsPath = "settings.json";

        private NodeRole currentRole;
        private CommChannel comm;
        private NodeMaster.NodeMaster? master;
        private NodeAgent.NodeAgent? agent;
        private NodeScheduler? scheduler;
        private SchedulerMode selectedMode = SchedulerMode.Auto;

        private ObservableCollection<AgentStatus> agentStatuses = new();
        private ObservableCollection<TaskInfo> tasks = new();
        private List<string> taskHistory = new();

        private ICollectionView? agentView;
        private string? _selectedAgentId;

        private System.Timers.Timer heartbeatTimer = default!;
        private DateTime masterStartTime;

        private int taskSequenceId = 0;
        private readonly Dictionary<string, List<(int SequenceId, string Result)>> computeResults = new();

        private NodeLinkSettings settings = new();

        // Agent numbering (sticky labels)
        private Dictionary<string, int> _agentNumberMap = new(StringComparer.OrdinalIgnoreCase);
        private const string AgentNumberMapPath = "agents.map.json";

        // Presence smoothing
        private readonly Dictionary<string, DateTime> _lastSeenUtc = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan RemoveAfter = TimeSpan.FromSeconds(60);

        // IDs / mDNS
        private readonly string _thisInstanceId = Guid.NewGuid().ToString();
        private MulticastService? mcast;
        private ServiceDiscovery? mdns;
        private ServiceProfile? mdnsMasterProfile;
        private ServiceProfile? mdnsAgentProfile;
        private System.Timers.Timer? agentDiscoveryTimer;

        private string? localAgentId;

        // CPU meter (no P/Invoke)
        private PerformanceCounter? _cpuTotalCounter;

        // ---- SoftThreads fields are provided by MainWindow.SoftThreads.cs partial ----
        // private SoftThreadDispatcher? _softDispatcher;
        // private AgentWorkQueue? _agentWork;

        // ---- Rolling master.log (last 100 lines) ----
        private readonly object _fileLogLock = new();
        private readonly Queue<string> _masterLogBuffer = new();
        private const int MasterLogMaxLines = 100;
        private const string MasterLogPath = "master.log";

        // v1.0.1: config + HTTP registration client for the new control plane
        private readonly NodeLinkConfig _cfg = NodeLinkConfig.Load();
        private readonly RegistrationClient _regClient = new();

        public MainWindow()
        {
            InitializeComponent();

            // -------- Role resolution (settings is the source of truth) --------
            LoadSettings();  // load BEFORE deciding role
            currentRole = (settings?.StartAsMaster == true) ? NodeRole.Master : NodeRole.Agent;

            // Legacy fallback ONLY if settings.json is missing/unreadable
            try
            {
                if (!System.IO.File.Exists(settingsPath))
                    currentRole = LoadRoleFromConfig(); // reads legacy node.config
            }
            catch { /* ignore and keep settings-based role */ }

            comm = new CommChannel();

            TaskListBox.ItemsSource = tasks;
            NodeGrid.ItemsSource = agentStatuses;

            agentView = CollectionViewSource.GetDefaultView(agentStatuses);
            if (agentView is ListCollectionView lcv) lcv.CustomSort = new AgentRowComparer();

            LoadAgentNumberMap();

            // Seed rolling file log buffer from previous run (Master only)
            LoadExistingMasterLog();

            InitializeRole(currentRole);
            UpdateRoleUI();
            SetupHeartbeat();

            comm.OnMessageReceived += HandleAgentMessage;

            if (currentRole == NodeRole.Master)
                SetupAgentDiscovery();
        }

        // -------- Sorting: Master first, then AgentId
        private sealed class AgentRowComparer : System.Collections.IComparer
        {
            public int Compare(object? x, object? y)
            {
                var ax = x as AgentStatus; var ay = y as AgentStatus;
                if (ax == null || ay == null) return 0;

                bool xMaster = ax.AgentId.Equals("Master", StringComparison.OrdinalIgnoreCase);
                bool yMaster = ay.AgentId.Equals("Master", StringComparison.OrdinalIgnoreCase);
                if (xMaster && !yMaster) return -1;
                if (!xMaster && yMaster) return 1;
                return StringComparer.OrdinalIgnoreCase.Compare(ax.AgentId, ay.AgentId);
            }
        }

        // ---------- Logging ----------
        private void Log(string message)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    LogBox.Items.Add($"{DateTime.Now:T}: {message}");
                    LogBox.ScrollIntoView(LogBox.Items[LogBox.Items.Count - 1]);
                });
            }
            catch { /* ignore UI logging errors */ }

            // Also keep a rolling file log (Master only)
            AppendMasterLog(message);
        }

        // Load existing master.log (if any) into the in-memory buffer on startup (Master only)
        private void LoadExistingMasterLog()
        {
            if (currentRole != NodeRole.Master) return;
            try
            {
                if (File.Exists(MasterLogPath))
                {
                    // Keep only the last 100 lines
                    var lines = new Queue<string>();
                    using var sr = new StreamReader(MasterLogPath);
                    while (!sr.EndOfStream)
                    {
                        lines.Enqueue(sr.ReadLine() ?? string.Empty);
                        while (lines.Count > MasterLogMaxLines)
                            lines.Dequeue();
                    }

                    lock (_fileLogLock)
                    {
                        _masterLogBuffer.Clear();
                        foreach (var line in lines) _masterLogBuffer.Enqueue(line);
                    }
                }
            }
            catch { /* ignore file errors */ }
        }

        // Append one line into the rolling buffer and write the entire buffer to disk (Master only)
        private void AppendMasterLog(string line)
        {
            if (currentRole != NodeRole.Master) return;

            try
            {
                var stamped = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}";
                lock (_fileLogLock)
                {
                    _masterLogBuffer.Enqueue(stamped);
                    while (_masterLogBuffer.Count > MasterLogMaxLines)
                        _masterLogBuffer.Dequeue();

                    // Persist the rolling window
                    File.WriteAllLines(MasterLogPath, _masterLogBuffer);
                }
            }
            catch { /* ignore file errors */ }
        }

        private void LoadSettings()
        {
            try
            {
                settings = NodeLinkSettings.Load(settingsPath);
                Log("Settings loaded.");
            }
            catch (Exception ex) { Log($"Failed to load settings: {ex.Message}"); }
        }

        private void SaveSettings()
        {
            try { settings.Save(settingsPath); Log("Settings saved."); }
            catch (Exception ex) { Log($"Failed to save settings: {ex.Message}"); }
        }

        // Prefer settings.StartAsMaster; fallback to node.config; default Agent
        private NodeRole LoadRoleFromConfig()
        {
            // Prefer settings.json if present (primary source of truth)
            try
            {
                if (File.Exists(settingsPath))
                {
                    var s = NodeLinkSettings.Load(settingsPath);
                    return s.StartAsMaster ? NodeRole.Master : NodeRole.Agent;
                }
            }
            catch { /* fall through to legacy */ }

            // Legacy fallback: node.config
            try
            {
                if (File.Exists(configPath))
                {
                    var roleText = File.ReadAllText(configPath).Trim();
                    if (Enum.TryParse(roleText, out NodeRole r)) return r;
                }
            }
            catch { /* ignore */ }

            // Safe default
            return NodeRole.Agent;
        }

        private async void InitializeRole(NodeRole role)
        {
            RoleLabel.Text = $"Current Role: {role}";

            EnsureMdnsStarted();
            string localIp = GetLocalIPAddress();

            if (role == NodeRole.Master)
            {
                comm.InitializeAsMaster("Master");

                var ip = ParseIp(localIp) ?? IPAddress.Loopback;
                mdnsMasterProfile = new ServiceProfile("NodeLinkMaster", ServiceTypeMaster, 5000, new[] { ip });
                mdns!.Advertise(mdnsMasterProfile);
                Log("mDNS advertised: _nodelink._tcp");

                master = new NodeMaster.NodeMaster(comm);
                masterStartTime = DateTime.Now;

                scheduler = new NodeScheduler(agentStatuses.ToList());
                scheduler.SetMode(selectedMode);

                MasterStatusLabel.Text = "Online";
                MasterStatusLabel.Foreground = Brushes.Green;

                UpdateOrAddAgentStatus(BuildLocalStatus("Master"));
                RefreshGridPreservingSelection();

                // Initialize SoftThreads on Master
                InitializeSoftThreadsForMaster();

                // Nudge agents already running
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    comm.Broadcast("WhoIsAlive");
                    Log("Broadcasted WhoIsAlive");
                });
            }
            else
            {
                localAgentId = $"Agent-{Environment.MachineName}";
                var ip = ParseIp(localIp) ?? IPAddress.Loopback;
                mdnsAgentProfile = new ServiceProfile($"NodeLinkAgent-{Environment.MachineName}", ServiceTypeAgent, 5000, new[] { ip });
                mdns!.Advertise(mdnsAgentProfile);
                Log("mDNS advertised: _nodelink-agent._tcp");

                // v1.0.1: Start the Agent HTTP control plane (health/register/ws stub)
                // This makes the Agent ready for Master auto-register via HTTP.
                AgentBootstrapper.Start(
                    getAgentId: () => localAgentId ?? $"Agent-{Environment.MachineName}",
                    getAgentName: () => Environment.MachineName,
                    getLocalIp: () => GetLocalIPAddress(),
                    log: s => Log(s)
                );

                // Discover Master
                string masterIp = await DiscoverMasterIpAsync();
                if (string.IsNullOrWhiteSpace(masterIp))
                {
                    masterIp = settings.MasterIp;
                    Log($"Discovery timed out; using saved Master IP: {masterIp}");
                }
                else
                {
                    settings.MasterIp = masterIp;
                    SaveSettings();
                    Log($"Discovered Master IP: {masterIp}");
                }

                comm.InitializeAsAgent(localAgentId, masterIp);
                comm.SetMasterIp(masterIp);

                UpdateOrAddAgentStatus(BuildLocalStatus(localAgentId));
                RefreshGridPreservingSelection();

                // Initialize SoftThreads on Agent
                InitializeSoftThreadsForAgent();

                // Announce presence (config + pulse)
                var cfg = BuildAgentConfig(localAgentId);
                comm.SendToMaster($"AgentConfig:{JsonSerializer.Serialize(cfg)}");
                var pulse = BuildAgentPulse(localAgentId);
                comm.SendToMaster($"AgentPulse:{JsonSerializer.Serialize(pulse)}");
            }
        }


        private IPAddress? ParseIp(string ip) => IPAddress.TryParse(ip, out var a) ? a : null;

        private string GetLocalIPAddress()
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.Description.Contains("virtual", StringComparison.OrdinalIgnoreCase)) continue;
                if (ni.Name.Contains("virtual", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                    if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        return ip.Address.ToString();
            }
            return "127.0.0.1";
        }

        private void UpdateRoleUI()
        {
            bool masterOnline = currentRole == NodeRole.Master || IsMasterOnline(settings.MasterIp);
            RegisterAgentButton.IsEnabled = currentRole == NodeRole.Master;
            DispatchTaskButton.IsEnabled = currentRole == NodeRole.Master;
            BroadcastButton.IsEnabled = currentRole == NodeRole.Master;
            StopAllTasksButton.IsEnabled = currentRole == NodeRole.Master;
            TaskInputBox.IsEnabled = currentRole == NodeRole.Master;
            CustomTaskButton.IsEnabled = currentRole == NodeRole.Master;

            TaskCountLabel.Text = tasks.Count.ToString();

            if (masterOnline && currentRole != NodeRole.Master)
            {
                MasterStatusLabel.Text = "Online";
                MasterStatusLabel.Foreground = Brushes.Green;
            }
            else if (currentRole != NodeRole.Master)
            {
                MasterStatusLabel.Text = "Offline";
                MasterStatusLabel.Foreground = Brushes.Red;
            }
        }

        public void RestartIntoRole(NodeRole role)
        {
            try
            {
                // Persist the desired role to settings (source of truth for next launch)
                settings.StartAsMaster = (role == NodeRole.Master);
                try { SaveSettings(); } catch { /* ignore */ }

                // Optional: keep legacy node.config in sync
                try { System.IO.File.WriteAllText("node.config", role.ToString()); } catch { /* ignore */ }

                // Clean down any background services
                try { DisposeSoftThreads(); } catch { /* ignore */ }
                try { StopMdns(); } catch { /* ignore */ }

                // Relaunch the current executable
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    System.Diagnostics.Process.Start(exePath);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to restart: {ex.Message}", "Restart Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Application.Current.Shutdown();
            }
        }

        private bool IsMasterOnline(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip) || ip == "Unknown") return false;
            try { using var ping = new Ping(); return ping.Send(ip, 1000).Status == IPStatus.Success; }
            catch { return false; }
        }

        private void LoadAgentNumberMap()
        {
            try
            {
                if (File.Exists(AgentNumberMapPath))
                {
                    var json = File.ReadAllText(AgentNumberMapPath);
                    var map = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                    if (map != null) _agentNumberMap = new Dictionary<string, int>(map, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch { }
        }

        private void SaveAgentNumberMap()
        {
            try
            {
                var json = JsonSerializer.Serialize(_agentNumberMap, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(AgentNumberMapPath, json);
            }
            catch { }
        }

        private int GetOrAssignAgentNumber(string agentId)
        {
            if (string.IsNullOrWhiteSpace(agentId)) return 0;
            if (_agentNumberMap.TryGetValue(agentId, out var n)) return n;
            int next = _agentNumberMap.Count == 0 ? 1 : Math.Max(1, _agentNumberMap.Values.Max() + 1);
            _agentNumberMap[agentId] = next;
            SaveAgentNumberMap();
            return next;
        }

        // ===================== Messaging =====================
        private void HandleAgentMessage(string agentId, string message)
        {
            Log($"Message from {agentId}: {message}");

            // Presence stamp
            if (!string.IsNullOrWhiteSpace(agentId) && !agentId.Equals("Master", StringComparison.OrdinalIgnoreCase))
                _lastSeenUtc[agentId] = DateTime.UtcNow;

            // Agent-side responses
            if (currentRole == NodeRole.Agent)
            {
                if (string.Equals(message, "RequestConfig", StringComparison.OrdinalIgnoreCase))
                {
                    var id = localAgentId ?? $"Agent-{Environment.MachineName}";
                    var cfg = BuildAgentConfig(id);
                    comm.SendToMaster($"AgentConfig:{JsonSerializer.Serialize(cfg)}");
                    return;
                }

                if (string.Equals(message, "WhoIsAlive", StringComparison.OrdinalIgnoreCase))
                {
                    var id = localAgentId ?? $"Agent-{Environment.MachineName}";
                    var cfg = BuildAgentConfig(id);
                    comm.SendToMaster($"AgentConfig:{JsonSerializer.Serialize(cfg)}");
                    var pulse = BuildAgentPulse(id);
                    comm.SendToMaster($"AgentPulse:{JsonSerializer.Serialize(pulse)}");
                    return;
                }

                if (message.StartsWith("Compute:", StringComparison.OrdinalIgnoreCase) ||
                    message.StartsWith("CustomTask:", StringComparison.OrdinalIgnoreCase))
                {
                    _agentWork?.Enqueue(message);
                    Log($"Queued work on Agent: {message}");
                    return;
                }
            }

            // Master: RegisterAck
            if (currentRole == NodeRole.Master && message.StartsWith("RegisterAck:", StringComparison.OrdinalIgnoreCase))
            {
                var first = message.IndexOf(':');
                var second = first >= 0 ? message.IndexOf(':', first + 1) : -1;
                if (first >= 0 && second > first)
                {
                    var ackAgentId = message.Substring(first + 1, second - first - 1).Trim();
                    var agentNumber = GetOrAssignAgentNumber(ackAgentId);

                    var row = agentStatuses.FirstOrDefault(a => a.AgentId.Equals(ackAgentId, StringComparison.OrdinalIgnoreCase));
                    if (row == null)
                    {
                        row = new AgentStatus { AgentId = ackAgentId, IsOnline = true, LastHeartbeat = DateTime.Now };
                        agentStatuses.Add(row);
                    }
                    else
                    {
                        row.IsOnline = true;
                        row.LastHeartbeat = DateTime.Now;
                    }
                    Log($"RegisterAck accepted from {ackAgentId} (Agent #{agentNumber})");
                    RefreshGridPreservingSelection();
                }
                return;
            }

            // Master: ComputeTestResult (supports ✓/✗ or OK/FAIL formats)
            if (currentRole == NodeRole.Master && message.StartsWith("ComputeTestResult:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // Formats supported:
                    // 1) ComputeTestResult:{agentId}:{✓|✗}
                    // 2) ComputeTestResult:{agentId}:{testId}:{OK|FAIL}
                    var parts = message.Split(':');
                    var aId = parts.Length > 1 ? parts[1].Trim() : agentId;
                    string symbol = "✓";

                    if (parts.Length == 3)
                    {
                        var tok = parts[2].Trim();
                        symbol = (tok.Equals("✓") || tok.Equals("OK", StringComparison.OrdinalIgnoreCase)) ? "✓" : "✗";
                    }
                    else if (parts.Length >= 4)
                    {
                        var tok = parts[3].Trim();
                        symbol = tok.Equals("OK", StringComparison.OrdinalIgnoreCase) ? "✓" : "✗";
                    }

                    var row = agentStatuses.FirstOrDefault(a => a.AgentId.Equals(aId, StringComparison.OrdinalIgnoreCase));
                    if (row != null)
                    {
                        row.SelfTestStatus = symbol;
                        row.IsOnline = true;
                        row.LastHeartbeat = DateTime.Now;
                        UpdateOrAddAgentStatus(row);
                        RefreshGridPreservingSelection();
                    }

                    _softDispatcher?.OnResultReceived(aId); // decrement in-flight if used
                    UpdateSoftThreadStats();

                    Log($"Self-test result from {aId}: {symbol}");
                }
                catch (Exception ex)
                {
                    Log($"ComputeTestResult parse error: {ex.Message}");
                }
                return;
            }

            // Master: static config (stable fields only)
            if (currentRole == NodeRole.Master && message.StartsWith("AgentConfig:", StringComparison.OrdinalIgnoreCase))
            {
                var json = message.Substring("AgentConfig:".Length);
                try
                {
                    var cfg = JsonSerializer.Deserialize<NodeCore.AgentConfig>(json);
                    if (cfg != null)
                    {
                        var row = agentStatuses.FirstOrDefault(a => a.AgentId.Equals(cfg.AgentId, StringComparison.OrdinalIgnoreCase))
                                  ?? new AgentStatus { AgentId = cfg.AgentId };

                        // Stable fields ONLY from AgentConfig
                        row.CpuLogicalCores = cfg.CpuLogicalCores;
                        row.HasGpu = cfg.HasGpu;
                        row.GpuModel = cfg.GpuModel ?? "";
                        row.GpuMemoryMB = cfg.GpuMemoryMB;
                        row.GpuCount = cfg.GpuCount;
                        row.InstanceId = cfg.InstanceId ?? "";
                        row.IsOnline = true;
                        row.LastHeartbeat = DateTime.Now;

                        UpdateOrAddAgentStatus(row);
                        RefreshGridPreservingSelection();
                        Log($"Config updated for {cfg.AgentId}");
                    }
                }
                catch (Exception ex) { Log($"Failed to parse AgentConfig: {ex.Message}"); }
                return;
            }

            // Everyone: volatile pulse (no zero flicker)
            if (message.StartsWith("AgentPulse:", StringComparison.OrdinalIgnoreCase))
            {
                var json = message.Substring("AgentPulse:".Length);
                try
                {
                    var p = JsonSerializer.Deserialize<NodeCore.AgentPulse>(json);
                    if (p != null)
                    {
                        _lastSeenUtc[p.AgentId] = DateTime.UtcNow;

                        var row = agentStatuses.FirstOrDefault(a => a.AgentId.Equals(p.AgentId, StringComparison.OrdinalIgnoreCase))
                                  ?? new AgentStatus { AgentId = p.AgentId };

                        float ApplyDelta(float current, float incoming, float minDelta = 1.5f)
                        {
                            if (float.IsNaN(incoming)) return current;
                            if (Math.Abs(current - incoming) < minDelta) return current;
                            return incoming;
                        }

                        row.CpuUsagePercent = ApplyDelta(row.CpuUsagePercent, p.CpuUsagePercent);
                        if (p.MemoryAvailableMB > 0) row.MemoryAvailableMB = p.MemoryAvailableMB;
                        row.GpuUsagePercent = row.HasGpu ? ApplyDelta(row.GpuUsagePercent, p.GpuUsagePercent) : 0f;
                        row.TaskQueueLength = p.TaskQueueLength;
                        row.NetworkMbps = p.NetworkMbps;
                        row.DiskReadMBps = p.DiskReadMBps;
                        row.DiskWriteMBps = p.DiskWriteMBps;
                        row.LastHeartbeat = p.LastHeartbeat == default ? DateTime.Now : p.LastHeartbeat;
                        row.IsOnline = true;

                        UpdateOrAddAgentStatus(row);
                        RefreshGridPreservingSelection();
                        UpdateSoftThreadStats();
                    }
                }
                catch (Exception ex) { Log($"Failed to parse AgentPulse: {ex.Message}"); }
                return;
            }

            // Legacy AgentStatus (kept)
            if (message.StartsWith("AgentStatus:", StringComparison.OrdinalIgnoreCase))
            {
                var json = message.Substring("AgentStatus:".Length);
                try
                {
                    var status = AgentStatus.FromJson(json);
                    if (status != null)
                    {
                        _lastSeenUtc[status.AgentId] = DateTime.UtcNow;
                        UpdateOrAddAgentStatus(status);
                        RefreshGridPreservingSelection();
                        Log($"Updated status for {status.AgentId}");
                    }
                }
                catch (Exception ex) { Log($"Failed to parse AgentStatus: {ex.Message}"); }
                return;
            }

            // ComputeResult collation (and in-flight decrement)
            if (message.StartsWith("ComputeResult:", StringComparison.OrdinalIgnoreCase))
            {
                _softDispatcher?.OnResultReceived(agentId);
                UpdateSoftThreadStats();

                var parts = message.Split(':');
                if (parts.Length > 2)
                {
                    var appTask = parts[1].Split('|');
                    if (appTask.Length > 2 && int.TryParse(appTask[1], out int sequenceId))
                    {
                        var appName = appTask[0];
                        var result = string.Join(":", parts, 2, parts.Length - 2);
                        if (!computeResults.ContainsKey(appName))
                            computeResults[appName] = new List<(int, string)>();
                        computeResults[appName].Add((sequenceId, result));
                        Log($"Received compute result for {appName} (sequence {sequenceId}) from {agentId}");
                        ReassembleComputeResults(appName);
                    }
                }
                return;
            }

            // Task list updates (kept simple)
            if (message.StartsWith("Result:", StringComparison.OrdinalIgnoreCase))
            {
                if (currentRole == NodeRole.Master)
                {
                    _softDispatcher?.OnResultReceived(agentId);
                    UpdateSoftThreadStats();
                }
                Log($"Task response: {message}");
                taskHistory.Add($"{DateTime.Now:T}: {message}");
                return;
            }
        }

        private void ReassembleComputeResults(string appName)
        {
            if (computeResults.TryGetValue(appName, out var results))
            {
                var ordered = results.OrderBy(r => r.SequenceId).Select(r => r.Result).ToList();
                Log($"Reassembled results for {appName}: {string.Join(", ", ordered)}");
            }
        }

        // ===================== UI actions =====================
        private void SchedulerModeDropdown_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb && cb.SelectedItem is ComboBoxItem item)
            {
                var modeText = item.Content?.ToString();
                if (Enum.TryParse(modeText, out SchedulerMode mode))
                {
                    selectedMode = mode;
                    if (currentRole == NodeRole.Master && scheduler != null)
                    {
                        scheduler.SetMode(mode);
                        Log($"Scheduler mode set to: {mode}");
                    }
                }
            }
        }

        private void Broadcast_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Send Ping with identity so Agents can unicast back
                var masterId = "Master";
                var masterIp = GetLocalIPAddress();
                if (!string.IsNullOrWhiteSpace(masterIp))
                {
                    settings.MasterIp = masterIp;
                    SaveSettings();
                }
                var seq = Environment.TickCount & 0x7FFFFFFF;

                if (!comm.BroadcastPing(masterId, masterIp, seq))
                {
                    Log("BroadcastPing failed; falling back to generic broadcast.");
                    comm.Broadcast($"Ping-{seq}");
                }
                Log($"Broadcast Ping sent: {masterId} {masterIp} #{seq}");

                // Fallback: unicast WhoIsAlive to known endpoints
                foreach (var a in agentStatuses.ToList())
                {
                    if (!string.IsNullOrWhiteSpace(a.AgentId) && a.AgentId != "Master")
                        comm.SendWhoIsAlive(a.AgentId);
                }
            }
            catch (Exception ex)
            {
                Log($"Broadcast failed: {ex.Message}");
            }
        }

        private void CustomTaskButton_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole != NodeRole.Master) { Log("Custom tasks only in Master mode."); return; }
            string task = TaskInputBox.Text;
            if (string.IsNullOrWhiteSpace(task)) { Log("No custom task entered."); return; }

            var agent = agentStatuses.FirstOrDefault(a => a.IsOnline && a.AgentId != "Master");
            if (agent == null) { Log("No online agents."); return; }

            string taskId = Guid.NewGuid().ToString();
            string payload = $"CustomTask:{taskId}:{task}";

            if (_softDispatcher != null)
                _softDispatcher.Enqueue(agent.AgentId, taskId, payload);
            else if (master != null)
                master.DispatchTaskToAgent(payload, agent.AgentId);
            else
                comm.SendToAgent(agent.AgentId, payload);

            Log($"Sent custom task: {task} to {agent.AgentId}");
            taskHistory.Add($"{DateTime.Now:T}: Sent {task} to {agent.AgentId}");
            tasks.Add(new TaskInfo { TaskId = taskId, Description = task, AgentId = agent.AgentId, Timestamp = DateTime.Now });
            TaskCountLabel.Text = tasks.Count.ToString();
        }

        private void StopTaskMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole != NodeRole.Master) { Log("Stop only in Master mode."); return; }
            if (TaskListBox.SelectedItem is not TaskInfo t) { Log("No task selected."); return; }

            master?.DispatchTaskToAgent($"StopTask:{t.TaskId}", t.AgentId);
            Log($"Requested stop {t.TaskId} on {t.AgentId}");
            taskHistory.Add($"{DateTime.Now:T}: Stop {t.TaskId} on {t.AgentId}");
        }

        // v1.0.1: Register button now uses HTTP control plane (RegistrationClient) instead of UDP
        private async void RegisterAgent_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole != NodeRole.Master) return;

            if (NodeGrid.SelectedItem is not AgentStatus sel || sel.AgentId == "Master")
            {
                Log("Select an Agent row first.");
                return;
            }

            // Use the configured default port (row has no ControlPort field)
            int port = _cfg.ControlPort;

            var ok = await _regClient.TryRegisterAsync(
                agentIp: sel.IpAddress ?? string.Empty,
                agentPort: port,
                masterId: "Master",                      // matches your current design
                masterIp: GetLocalIPAddress(),
                bearerToken: _cfg.AuthToken,
                log: Log);

            Log(ok
                ? $"Registered {sel.AgentId} at {sel.IpAddress}:{port} via HTTP ✅"
                : $"Failed to register {sel.AgentId} at {sel.IpAddress}:{port} via HTTP");
        }


        private void UpdateAgentConfig_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole != NodeRole.Master) { Log("Only in Master mode."); return; }
            if (NodeGrid.SelectedItem is not AgentStatus row || row.AgentId == "Master")
            {
                Log("Select an Agent row first."); return;
            }
            var ok = comm.SendToAgent(row.AgentId, "RequestConfig");
            Log(ok ? $"Requested config from {row.AgentId}" : $"Could not reach {row.AgentId}");
        }

        private void StopAllTasksButton_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole != NodeRole.Master) { Log("Only in Master mode."); return; }
            var ids = agentStatuses.Where(a => a.AgentId != "Master" && a.IsOnline).Select(a => a.AgentId).ToList();
            foreach (var id in ids) master?.DispatchTaskToAgent("StopAllTasks", id);
            Log("Requested StopAllTasks on all online agents");
        }

        private void DispatchTask_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole != NodeRole.Master) { Log("Only in Master mode."); return; }

            var target = agentStatuses.FirstOrDefault(a => a.IsOnline && a.AgentId != "Master");
            if (target == null) { Log("No online agents to dispatch."); return; }

            var seq = taskSequenceId++;
            string taskId = $"task-{seq}";
            string payload = $"Compute:QuickSmoke|{seq}|noop";

            if (_softDispatcher != null)
                _softDispatcher.Enqueue(target.AgentId, taskId, payload);
            else if (master != null)
                master.DispatchTaskToAgent(payload, target.AgentId);
            else
                comm.SendToAgent(target.AgentId, payload);

            Log($"Dispatched test task to {target.AgentId}: {payload}");
        }

        private async void ComputeTest_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole != NodeRole.Master) { Log("Only in Master mode."); return; }
            var targets = agentStatuses.Where(a => a.IsOnline && a.AgentId != "Master").ToList();
            if (targets.Count == 0) { Log("No online agents to test."); return; }

            var runId = Guid.NewGuid().ToString("N");
            foreach (var a in targets)
            {
                var payload = $"Compute:QuickSmoke|0|noop";
                var tid = $"smoke-{runId}-{a.AgentId}";
                if (_softDispatcher != null) _softDispatcher.Enqueue(a.AgentId, tid, payload);
                else comm.SendToAgent(a.AgentId, payload);
            }

            await Task.Delay(1500);
            Log("ComputeTest round dispatched.");
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var sw = new SettingsWindow(settings, agentStatuses.ToList(), false);
            sw.Owner = this;
            sw.ShowDialog();
            ApplySettings();
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                DisposeSoftThreads();
                StopMdns();
                SaveSettings();
            }
            catch { }
            Application.Current.Shutdown();
        }

        private void ApplySettings()
        {
            heartbeatTimer.Interval = Math.Max(1000, settings.HeartbeatIntervalMs);
            Log($"Heartbeat: {heartbeatTimer.Interval} ms, Master IP: {settings.MasterIp}");
        }

        // NEW: Hamburger → Open Logs
        private void OpenLogsMenu_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = System.IO.Path.GetFullPath(MasterLogPath);
                var dir = System.IO.Path.GetDirectoryName(path);

                if (System.IO.File.Exists(path))
                {
                    // Open Explorer with the log file selected
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{path}\"",
                        UseShellExecute = true
                    });
                }
                else if (!string.IsNullOrEmpty(dir))
                {
                    // Ensure folder exists, then open it
                    System.IO.Directory.CreateDirectory(dir);
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = dir,
                        UseShellExecute = true
                    });
                }
                else
                {
                    MessageBox.Show(this, "No log file or folder to open yet.", "Logs",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't open log location:\n{ex.Message}", "Logs",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private void HelpMenu_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Try to open a bundled help file if present
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var html = System.IO.Path.Combine(baseDir, "HELP.html");
                var md = System.IO.Path.Combine(baseDir, "HELP.md");
                var readmeHtml = System.IO.Path.Combine(baseDir, "README.html");
                var readmeMd = System.IO.Path.Combine(baseDir, "README.md");

                string? toOpen = null;
                if (File.Exists(html)) toOpen = html;
                else if (File.Exists(md)) toOpen = md;
                else if (File.Exists(readmeHtml)) toOpen = readmeHtml;
                else if (File.Exists(readmeMd)) toOpen = readmeMd;

                if (toOpen != null)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = toOpen,
                        UseShellExecute = true
                    });
                }
                else
                {
                    MessageBox.Show(this,
                        "Help is coming soon. For now, check the log and Settings for tips.",
                        "Help",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't open help: {ex.Message}", "Help",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ===================== Heartbeat (Master presence + Agent pulse) =====================
        private void SetupHeartbeat()
        {
            heartbeatTimer = new System.Timers.Timer(Math.Max(1000, settings.HeartbeatIntervalMs)); // if Math.max errors in your env, change to Math.Max
            heartbeatTimer.Elapsed += (s, e) => Dispatcher.Invoke(UpdateHeartbeat);
            heartbeatTimer.AutoReset = true;
            heartbeatTimer.Start();
        }

        private void UpdateHeartbeat()
        {
            if (currentRole == NodeRole.Master)
            {
                var nowUtc = DateTime.UtcNow;

                // Ensure Master row
                UpdateOrAddAgentStatus(BuildLocalStatus("Master"));

                // Presence smoothing
                foreach (var a in agentStatuses.ToList())
                {
                    if (a.AgentId.Equals("Master", StringComparison.OrdinalIgnoreCase)) continue;

                    if (_lastSeenUtc.TryGetValue(a.AgentId, out var seen))
                    {
                        var silence = nowUtc - seen;
                        if (silence <= OfflineAfter) a.IsOnline = true;
                        else if (silence <= RemoveAfter) a.IsOnline = false;
                        else
                        {
                            // Remove if not selected
                            if (!(NodeGrid?.SelectedItem is AgentStatus sel && sel.AgentId.Equals(a.AgentId, StringComparison.OrdinalIgnoreCase)))
                                agentStatuses.Remove(a);
                        }
                    }
                    else a.IsOnline = false;
                }

                MasterUptimeLabel.Text = (DateTime.Now - masterStartTime).ToString(@"hh\:mm\:ss");
                RefreshGridPreservingSelection();

                // Simple alerts
                Dispatcher.Invoke(() =>
                {
                    AlertBox.Items.Clear();
                    foreach (var s in agentStatuses)
                    {
                        if (s.AgentId == "Master") continue;
                        if (!s.IsOnline) AlertBox.Items.Add($"{s.AgentId} is offline");
                        else if (s.CpuUsagePercent > 85) AlertBox.Items.Add($"{s.AgentId} high CPU: {s.CpuUsagePercent:F0}%");
                    }
                });
                return;
            }
            else
            {
                var id = localAgentId ?? $"Agent-{Environment.MachineName}";
                var pulse = BuildAgentPulse(id);
                comm.SendToMaster($"AgentPulse:{JsonSerializer.Serialize(pulse)}");
                _lastSeenUtc[id] = DateTime.UtcNow;

                // Update local UI row for Agent
                var local = BuildLocalStatus(id);
                local.CpuUsagePercent = pulse.CpuUsagePercent;
                local.GpuUsagePercent = pulse.GpuUsagePercent;
                local.MemoryAvailableMB = pulse.MemoryAvailableMB;
                local.TaskQueueLength = pulse.TaskQueueLength;
                local.LastHeartbeat = pulse.LastHeartbeat;

                UpdateOrAddAgentStatus(local);
                RefreshGridPreservingSelection();
            }
        }

        // ===================== mDNS =====================
        private void EnsureMdnsStarted()
        {
            if (mcast == null) { mcast = new MulticastService(); mcast.Start(); }
            if (mdns == null)
            {
                mdns = new ServiceDiscovery(mcast);
                mdns.ServiceDiscovered += (s, serviceName) => { try { mdns.QueryServiceInstances(serviceName); } catch { } };
                mdns.ServiceInstanceDiscovered += Mdns_ServiceInstanceDiscovered;
                mdns.ServiceInstanceShutdown += Mdns_ServiceInstanceShutdown;
            }
            try { mdns.QueryAllServices(); } catch { }
        }

        private void Mdns_ServiceInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
        {
            var fqdn = e.ServiceInstanceName?.ToString() ?? "";
            var ip = TryGetIp(e);
            if (string.IsNullOrEmpty(fqdn) || string.IsNullOrEmpty(ip)) return;

            if (fqdn.Contains("." + ServiceTypeMaster + ".", StringComparison.OrdinalIgnoreCase))
            {
                settings.MasterIp = ip!;
                SaveSettings();
                MasterStatusLabel.Text = "Online";
                MasterStatusLabel.Foreground = Brushes.Green;
                Log($"Discovered Master at {ip}");
                if (currentRole != NodeRole.Master) comm.SetMasterIp(ip!);
                return;
            }

            if (fqdn.Contains("." + ServiceTypeAgent + ".", StringComparison.OrdinalIgnoreCase))
            {
                var agentId = NormalizeAgentIdFromInstance(fqdn);
                _lastSeenUtc[agentId] = DateTime.UtcNow;
                comm.UpdateAgentEndpoint(agentId, ip!);

                var row = new AgentStatus
                {
                    AgentId = agentId,
                    IsOnline = true,
                    IpAddress = ip!,
                    LastHeartbeat = DateTime.Now
                    // ControlPort can be set from mDNS TXT later; default picked from _cfg when needed
                };
                UpdateOrAddAgentStatus(row);
                RefreshGridPreservingSelection();
                Log($"Discovered Agent {agentId} at {ip}");

                // v1.0.1 [B]: Auto-register via HTTP control plane (Master side only; harmless if called otherwise)
                MasterBootstrapper.OnAgentDiscovered(row);
            }
        }

        private void Mdns_ServiceInstanceShutdown(object? sender, ServiceInstanceShutdownEventArgs e)
        {
            var fqdn = e.ServiceInstanceName.ToString();
            if (string.IsNullOrEmpty(fqdn)) return;

            if (fqdn.Contains("." + ServiceTypeAgent + ".", StringComparison.OrdinalIgnoreCase))
            {
                var agentId = fqdn.Split('.')[0];
                var existing = agentStatuses.FirstOrDefault(a => a.AgentId == agentId);
                if (existing != null && agentId != "Master")
                {
                    existing.IsOnline = false;
                    RefreshGridPreservingSelection();
                    Log($"Service removed: {agentId}");
                }
            }
        }

        private static string? TryGetIp(ServiceInstanceDiscoveryEventArgs e)
        {
            var msg = e.Message; if (msg == null) return null;

            var srv = msg.AdditionalRecords.OfType<SRVRecord>().FirstOrDefault();
            if (srv == null) return null;

            var a = msg.AdditionalRecords.OfType<ARecord>().FirstOrDefault(r => r.Name == srv.Target);
            if (a != null) return a.Address.ToString();

            var aaaa = msg.AdditionalRecords.OfType<AAAARecord>().FirstOrDefault(r => r.Name == srv.Target);
            return aaaa?.Address.ToString();
        }

        private async Task<string?> DiscoverMasterIpAsync()
        {
            try
            {
                EnsureMdnsStarted();

                var tcs = new TaskCompletionSource<string?>();
                EventHandler<ServiceInstanceDiscoveryEventArgs>? handler = null;

                handler = (s, e) =>
                {
                    var fqdn = e.ServiceInstanceName.ToString();
                    if (!string.IsNullOrEmpty(fqdn) &&
                        fqdn.Contains("." + ServiceTypeMaster + ".", StringComparison.OrdinalIgnoreCase))
                    {
                        var ip = TryGetIp(e);
                        if (!string.IsNullOrEmpty(ip))
                        {
                            tcs.TrySetResult(ip);
                            if (mdns != null) mdns.ServiceInstanceDiscovered -= handler!;
                        }
                    }
                };

                mdns!.ServiceInstanceDiscovered += handler;
                mdns.QueryServiceInstances(ServiceTypeMaster);

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                if (completed == tcs.Task && !string.IsNullOrEmpty(tcs.Task.Result))
                    return tcs.Task.Result;

                if (mdns != null) mdns.ServiceInstanceDiscovered -= handler!;
            }
            catch (Exception ex) { Log($"mDNS Master discovery error: {ex.Message}"); }

            return null;
        }

        private async Task DiscoverAgentsAsync()
        {
            try { EnsureMdnsStarted(); mdns!.QueryServiceInstances(ServiceTypeAgent); await Task.Delay(200); }
            catch (Exception ex) { Log($"mDNS Agent discovery error: {ex.Message}"); }
        }

        private void SetupAgentDiscovery()
        {
            agentDiscoveryTimer = new System.Timers.Timer(10000);
            agentDiscoveryTimer.Elapsed += (s, e) => DiscoverAgentsAsync().GetAwaiter().GetResult();
            agentDiscoveryTimer.AutoReset = true;
            agentDiscoveryTimer.Start();
        }

        private void StopMdns()
        {
            try
            {
                if (mdnsMasterProfile != null) mdns?.Unadvertise(mdnsMasterProfile);
                if (mdnsAgentProfile != null) mdns?.Unadvertise(mdnsAgentProfile);
                mdns?.Dispose();
                mcast?.Stop();
                mcast?.Dispose();
            }
            catch { }
            mcast = null; mdns = null; mdnsMasterProfile = null; mdnsAgentProfile = null;
        }

        // ===================== Probes / Builders =====================

        // CPU percent without P/Invoke
        private float GetSystemCpuUsagePercent()
        {
            try
            {
                if (_cpuTotalCounter == null)
                {
                    _cpuTotalCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                    _ = _cpuTotalCounter.NextValue(); // prime
                    return 0f;
                }
                var v = _cpuTotalCounter.NextValue();
                if (float.IsNaN(v)) return 0f;
                return Math.Max(0f, Math.Min(100f, v));
            }
            catch { return 0f; }
        }

        private float GetAvailableMemoryMB()
        {
            var msex = new MEMORYSTATUSEX();
            msex.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (GlobalMemoryStatusEx(ref msex))
                return (float)(msex.ullAvailPhys / (1024.0 * 1024.0));
            return 0f;
        }

        private (bool hasGpu, string model, int vramMB) GetPrimaryGpuInfo()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM, PNPDeviceID FROM Win32_VideoController");
                foreach (ManagementObject mo in searcher.Get())
                {
                    string pnp = mo["PNPDeviceID"]?.ToString() ?? "";
                    if (!pnp.Contains("PCI", StringComparison.OrdinalIgnoreCase)) continue;

                    string model = mo["Name"]?.ToString() ?? "GPU";
                    long bytes = 0;
                    var ramObj = mo["AdapterRAM"];
                    if (ramObj != null) { try { bytes = Convert.ToInt64(ramObj); } catch { bytes = 0; } }
                    int mb = (int)Math.Round(bytes / (1024.0 * 1024.0));
                    return (mb > 0, model, mb);
                }

                using var searcher2 = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
                var first = searcher2.Get().Cast<ManagementObject>().FirstOrDefault();
                if (first != null)
                {
                    string model = first["Name"]?.ToString() ?? "GPU";
                    long bytes = 0;
                    var ramObj = first["AdapterRAM"];
                    if (ramObj != null) { try { bytes = Convert.ToInt64(ramObj); } catch { bytes = 0; } }
                    int mb = (int)Math.Round(bytes / (1024.0 * 1024.0));
                    return (mb > 0, model, mb);
                }
            }
            catch { }
            return (false, string.Empty, 0);
        }

        private void EnsureGpuCounters()
        {
            if (_gpuCountersInit) return;
            try
            {
                var cat = new PerformanceCounterCategory("GPU Engine");
                var instances = cat.GetInstanceNames();
                _gpuCounters = instances
                    .Where(n => n.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) >= 0)
                    .SelectMany(n => cat.GetCounters(n))
                    .Where(c => c.CounterName == "Utilization Percentage")
                    .ToList();

                foreach (var c in _gpuCounters) _ = c.NextValue();
            }
            catch { _gpuCounters = null; }
            _gpuCountersInit = true;
        }
        private List<PerformanceCounter>? _gpuCounters;
        private bool _gpuCountersInit;

        private float GetGpuUtilizationPercent()
        {
            try
            {
                if (!_gpuCountersInit) EnsureGpuCounters();
                if (_gpuCounters == null || _gpuCounters.Count == 0) return 0f;

                float sum = 0f;
                foreach (var c in _gpuCounters)
                {
                    try { sum += c.NextValue(); } catch { }
                }
                return Math.Max(0, Math.Min(100, sum));
            }
            catch { return 0f; }
        }

        private int GetGpuCountLocal()
        {
            try
            {
                using var s = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_VideoController");
                int count = 0;
                foreach (ManagementObject mo in s.Get())
                {
                    string name = mo["Name"]?.ToString() ?? "";
                    string pnp = mo["PNPDeviceID"]?.ToString() ?? "";
                    if (pnp.IndexOf("PCI", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        name.IndexOf("Microsoft Basic Display", StringComparison.OrdinalIgnoreCase) < 0)
                        count++;
                }
                return count;
            }
            catch { return 0; }
        }

        private AgentStatus BuildLocalStatus(string agentId)
        {
            var (hasGpu, gpuModel, vramMB) = GetPrimaryGpuInfo();
            var gpuUtil = GetGpuUtilizationPercent();

            return new AgentStatus
            {
                AgentId = agentId,
                IsOnline = true,
                IpAddress = GetLocalIPAddress(),
                CpuUsagePercent = GetSystemCpuUsagePercent(),
                GpuUsagePercent = gpuUtil,
                CpuLogicalCores = Environment.ProcessorCount,
                MemoryAvailableMB = GetAvailableMemoryMB(),
                NetworkMbps = 0f,
                HasFileAccess = true,
                AvailableFiles = Array.Empty<string>(),
                TaskQueueLength = tasks.Count(t => t.AgentId == agentId),
                LastHeartbeat = DateTime.Now,

                HasGpu = hasGpu,
                HasCuda = false,
                GpuModel = gpuModel,
                GpuMemoryMB = vramMB,

                GpuCount = GetGpuCountLocal(),
                InstanceId = _thisInstanceId,

                LastTaskDuration = TimeSpan.Zero,
                DiskReadMBps = 0f,
                DiskWriteMBps = 0f
            };
        }

        private NodeCore.AgentConfig BuildAgentConfig(string agentId)
        {
            var (hasGpu, gpuModel, vramMB) = GetPrimaryGpuInfo();
            return new NodeCore.AgentConfig
            {
                AgentId = agentId,
                CpuLogicalCores = Environment.ProcessorCount,
                RamTotalMB = GetTotalPhysicalRamMB(),
                HasGpu = hasGpu,
                GpuModel = gpuModel,
                GpuMemoryMB = vramMB,
                GpuCount = GetGpuCountLocal(),
                OsVersion = Environment.OSVersion.ToString(),
                InstanceId = _thisInstanceId
            };
        }

        private NodeCore.AgentPulse BuildAgentPulse(string agentId)
        {
            return new NodeCore.AgentPulse
            {
                AgentId = agentId,
                CpuUsagePercent = GetSystemCpuUsagePercent(),
                MemoryAvailableMB = GetAvailableMemoryMB(),
                GpuUsagePercent = GetGpuUtilizationPercent(),
                TaskQueueLength = tasks.Count(t => t.AgentId == agentId),
                NetworkMbps = 0f,
                DiskReadMBps = 0f,
                DiskWriteMBps = 0f,
                LastHeartbeat = DateTime.Now
            };
        }

        private float GetTotalPhysicalRamMB()
        {
            try
            {
                using var cs = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
                foreach (ManagementObject mo in cs.Get())
                {
                    if (mo["TotalVisibleMemorySize"] != null)
                    {
                        double kb = Convert.ToDouble(mo["TotalVisibleMemorySize"]);
                        return (float)(kb / 1024.0);
                    }
                }
            }
            catch { }
            return 0f;
        }

        // ---------- Grid helpers ----------
        private void RefreshGridPreservingSelection()
        {
            try
            {
                if (NodeGrid.SelectedItem is AgentStatus sel)
                    _selectedAgentId = sel.AgentId;

                agentView?.Refresh();

                if (!string.IsNullOrWhiteSpace(_selectedAgentId))
                {
                    var match = agentStatuses.FirstOrDefault(a => a.AgentId.Equals(_selectedAgentId, StringComparison.OrdinalIgnoreCase));
                    if (match != null) NodeGrid.SelectedItem = match;
                }
            }
            catch { }
        }

        private AgentStatus UpdateOrAddAgentStatus(AgentStatus incoming)
        {
            var existing = agentStatuses.FirstOrDefault(a => a.AgentId.Equals(incoming.AgentId, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                agentStatuses.Add(incoming);
                return incoming;
            }

            existing.IsOnline = incoming.IsOnline;
            existing.IpAddress = incoming.IpAddress;
            existing.CpuUsagePercent = incoming.CpuUsagePercent;
            existing.GpuUsagePercent = incoming.GpuUsagePercent;
            existing.CpuLogicalCores = incoming.CpuLogicalCores;
            existing.MemoryAvailableMB = incoming.MemoryAvailableMB;
            existing.NetworkMbps = incoming.NetworkMbps;
            existing.HasFileAccess = incoming.HasFileAccess;
            existing.AvailableFiles = incoming.AvailableFiles;
            existing.TaskQueueLength = incoming.TaskQueueLength;
            existing.LastHeartbeat = incoming.LastHeartbeat;

            existing.HasGpu = incoming.HasGpu;
            existing.HasCuda = incoming.HasCuda;
            existing.GpuModel = incoming.GpuModel;
            existing.GpuMemoryMB = incoming.GpuMemoryMB;
            existing.GpuCount = incoming.GpuCount;
            existing.InstanceId = incoming.InstanceId;

            existing.LastTaskDuration = incoming.LastTaskDuration;
            existing.DiskReadMBps = incoming.DiskReadMBps;
            existing.DiskWriteMBps = incoming.DiskWriteMBps;

            existing.SelfTestStatus = incoming.SelfTestStatus;

            return existing;
        }

        private static string NormalizeAgentIdFromInstance(string? instanceName)
        {
            if (string.IsNullOrWhiteSpace(instanceName)) return "Agent-Unknown";
            var left = instanceName;
            var dot = left.IndexOf('.');
            if (dot > 0) left = left[..dot];

            const string prefix = "NodeLinkAgent-";
            if (left.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                left = "Agent-" + left[prefix.Length..];

            if (!left.StartsWith("Agent-", StringComparison.OrdinalIgnoreCase))
                left = "Agent-" + left;

            return left;
        }

        // Extra: About + no-op security refresh for Settings window
        private void AboutMenu_Click(object sender, RoutedEventArgs e)
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var name = asm.GetName();
            var ver = name.Version?.ToString() ?? "0.0.0.0";
            MessageBox.Show(this, $"NodeLinkUI\nVersion: {ver}", "About", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        public void RefreshCommunicationSecurity() { /* v1-lite: no-op */ }

        // Memory P/Invoke (kept)
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }
    }
}











