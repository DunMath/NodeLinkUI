// MainWindow.xaml.cs — Lean build (no SoftThreads, no in-flight guard)
// - AppRun fast path wired (agent-side)
// - Orchestrator metrics (RTT/QueueWait) hooked
// - Registration via local HTTP helper in this file
// - Compute test and distributed benchmark retained for diagnostics

using Makaretu.Dns;
using NodeAgent;
using NodeComm;
using NodeCore;
using NodeCore.Config;
using NodeCore.Exec;
using NodeCore.Orchestration;
using NodeCore.Protocol;
using NodeCore.Scheduling;
using NodeCore.Exec;
using NodeMaster;
using NodeMaster.Distrib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Http;
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

        private Dictionary<string, int> _agentNumberMap = new(StringComparer.OrdinalIgnoreCase);
        private const string AgentNumberMapPath = "agents.map.json";

        private readonly Dictionary<string, DateTime> _lastSeenUtc = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan RemoveAfter = TimeSpan.FromSeconds(60);

        private readonly HashSet<string> _registeredAgents = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _lastDiscoveryLog = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan DiscoveryLogMinGap = TimeSpan.FromSeconds(30);

        private const bool ENABLE_AUTO_REGISTER = false;
        private readonly HashSet<string> _shownRegisterDialog = new(StringComparer.OrdinalIgnoreCase);

        private readonly string _thisInstanceId = Guid.NewGuid().ToString();
        private MulticastService? mcast;
        private ServiceDiscovery? mdns;
        private ServiceProfile? mdnsMasterProfile;
        private ServiceProfile? mdnsAgentProfile;
        private System.Timers.Timer? agentDiscoveryTimer;

        private string? localAgentId;

        private PerformanceCounter? _cpuTotalCounter;
        private List<PerformanceCounter>? _gpuCounters;
        private bool _gpuCountersInit;

        private readonly object _fileLogLock = new();
        private readonly Queue<string> _masterLogBuffer = new();
        private const int MasterLogMaxLines = 100;
        private const string MasterLogPath = "master.log";

        private readonly NodeLinkConfig _cfg = NodeLinkConfig.Load();
        private MasterOrchestrator? _masterOrchestrator;

        //// master-side local metrics
        // pass-through for now

        private DecisionCoordinator? _decider;
        private LocalExecutor? _localExec;

        private DefaultOrchestrator? _orchestrator;
        private DefaultResultCollator? _collator;

        private AgentBootstrapper? _agentBootstrap;

        private void OnUI(Action work)
        {
            if (Dispatcher.CheckAccess()) work();
            else Dispatcher.BeginInvoke(work);
        }

        public MainWindow()
        {
            InitializeComponent();

            LoadSettings();
            currentRole = (settings?.StartAsMaster == true) ? NodeRole.Master : NodeRole.Agent;

            try
            {
                if (!System.IO.File.Exists(settingsPath))
                    currentRole = LoadRoleFromConfig();
            }
            catch { }

            comm = new CommChannel();

            TaskListBox.ItemsSource = tasks;
            NodeGrid.ItemsSource = agentStatuses;

            agentView = CollectionViewSource.GetDefaultView(agentStatuses);
            if (agentView is ListCollectionView lcv)
                lcv.CustomSort = new AgentRowComparer();

            LoadAgentNumberMap();
            LoadExistingMasterLog();

            InitializeRole(currentRole);
            UpdateRoleUI();
            SetupHeartbeat();

            comm.OnMessageReceived += HandleAgentMessage;

            if (currentRole == NodeRole.Master)
            {
                SetupAgentDiscovery();
            }
        }

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

        private void Log(string message)
        {
            try
            {
                OnUI(() =>
                {
                    LogBox.Items.Add($"{DateTime.Now:T}: {message}");
                    LogBox.ScrollIntoView(LogBox.Items[LogBox.Items.Count - 1]);
                });
            }
            catch { }

            AppendMasterLog(message);
        }

        private void LoadExistingMasterLog()
        {
            if (currentRole != NodeRole.Master) return;
            try
            {
                if (File.Exists(MasterLogPath))
                {
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
            catch { }
        }

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
                    File.WriteAllLines(MasterLogPath, _masterLogBuffer);
                }
            }
            catch { }
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

        private NodeRole LoadRoleFromConfig()
        {
            try
            {
                if (File.Exists(settingsPath))
                {
                    var s = NodeLinkSettings.Load(settingsPath);
                    return s.StartAsMaster ? NodeRole.Master : NodeRole.Agent;
                }
            }
            catch { }
            try
            {
                if (File.Exists(configPath))
                {
                    var roleText = File.ReadAllText(configPath).Trim();
                    if (Enum.TryParse(roleText, out NodeRole r)) return r;
                }
            }
            catch { }
            return NodeRole.Agent;
        }

        private async void InitializeRole(NodeRole role)
        {
            RoleLabel.Text = $"Current Role: {role}";

            EnsureMdnsStarted();

            var localIp = GetLocalIPAddress();
            var ipAddr = ParseIp(localIp) ?? IPAddress.Loopback;

            if (role == NodeRole.Master)
            {
                comm.InitializeAsMaster("Master");

                mdnsMasterProfile = new ServiceProfile("NodeLinkMaster", ServiceTypeMaster, 5000, new[] { ipAddr });
                mdns!.Advertise(mdnsMasterProfile);
                Log("mDNS advertised: _nodelink._tcp");

                master = new NodeMaster.NodeMaster(comm);
                masterStartTime = DateTime.Now;

                scheduler = new NodeScheduler(agentStatuses.ToList());
                scheduler.SetMode(selectedMode);

                MasterStatusLabel.Text = "Online";
                MasterStatusLabel.Foreground = Brushes.Green;

                OnUI(() =>
                {
                    UpdateOrAddAgentStatus(BuildLocalStatus("Master"));
                    RefreshGridPreservingSelection();
                });

                InitializeOrchestration();

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

                mdnsAgentProfile = new ServiceProfile($"NodeLinkAgent-{Environment.MachineName}", ServiceTypeAgent, 5000, new[] { ipAddr });
                mdns!.Advertise(mdnsAgentProfile);
                Log("mDNS advertised: _nodelink-agent._tcp");

                string? masterIp = await DiscoverMasterIpAsync();
                if (string.IsNullOrWhiteSpace(masterIp))
                {
                    masterIp = settings?.MasterIp;
                    if (!string.IsNullOrWhiteSpace(masterIp))
                        Log($"Discovery timed out; using saved Master IP: {masterIp}");
                    else
                        Log("Discovery timed out and no saved Master IP.");
                }
                else
                {
                    if (settings != null)
                    {
                        settings.MasterIp = masterIp;
                        SaveSettings();
                    }
                    Log($"Discovered Master IP: {masterIp}");
                }

                comm.InitializeAsAgent(localAgentId, masterIp ?? "127.0.0.1");
                comm.SetMasterIp(masterIp ?? "127.0.0.1");

                try
                {
                    var commAdapter = new AgentCommAdapter(comm);
                    _agentBootstrap = new AgentBootstrapper(commAdapter, localAgentId);

                    // FAST PATH: route "AppRun:" frames straight to the agent executor
                    comm.AppRunSink = _agentBootstrap.OnWireMessage;

                    _agentBootstrap.Start(masterIp ?? "127.0.0.1");
                }
                catch (Exception ex)
                {
                    Log($"AgentBootstrapper.Start failed: {ex.Message}");
                }

                OnUI(() =>
                {
                    UpdateOrAddAgentStatus(BuildLocalStatus(localAgentId));
                    RefreshGridPreservingSelection();
                });

                var cfg = BuildAgentConfig(localAgentId);
                comm.SendToMaster($"AgentConfig:{System.Text.Json.JsonSerializer.Serialize(cfg)}");
                var pulse = BuildAgentPulse(localAgentId);
                comm.SendToMaster($"AgentPulse:{System.Text.Json.JsonSerializer.Serialize(pulse)}");
            }
        }

        private void InitializeOrchestration()
        {
            if (currentRole != NodeRole.Master) return;

            // Result collation (unchanged)
            _collator = new DefaultResultCollator();
            _collator.JobCompleted += (app, runId, ordered) =>
            {
                Log($"Job complete: {app} run={runId} ({ordered.Count} results).");
            };

            // Orchestrator (unchanged)
            _orchestrator = new DefaultOrchestrator(() => agentStatuses.ToList(), maxOutstandingPerAgent: 1);

            // MasterOrchestrator (network send/receive owner) (unchanged)
            _masterOrchestrator ??= new MasterOrchestrator(
                comm,
                () => agentStatuses,   // live snapshot source
                Log
            );

            // Timing → orchestrator metrics (unchanged)
            _masterOrchestrator.OnWillSend = (agentId, corr, wire) =>
            {
                try { _orchestrator.MarkSentNow(agentId, corr, wire); }
                catch (Exception ex) { Log($"MarkSentNow error: {ex.Message}"); }
            };
            _masterOrchestrator.OnResultForMetrics = env =>
            {
                try { _orchestrator.OnResult(env); }
                catch (Exception ex) { Log($"OnResult error: {ex.Message}"); }
            };

            // Reflect agent ban/unban in UI (unchanged)
            _masterOrchestrator.OnAgentBanChanged = (agentId, isDegraded) =>
            {
                OnUI(() =>
                {
                    var row = agentStatuses.FirstOrDefault(a =>
                        a.AgentId.Equals(agentId, StringComparison.OrdinalIgnoreCase));
                    if (row != null)
                    {
                        row.IsDegraded = isDegraded;
                        RefreshGridPreservingSelection();
                    }
                });
            };

            // UI hooks for remote RTT (Latency) (unchanged)
            _orchestrator.LatencyMeasured += (agentId, app, seq, ms) =>
            {
                OnUI(() =>
                {
                    var row = agentStatuses.FirstOrDefault(a =>
                        a.AgentId.Equals(agentId, StringComparison.OrdinalIgnoreCase));
                    if (row == null) return;

                    row.LastLatencyMs = (long)Math.Round(ms);

                    var n = Math.Max(0, row.Samples);
                    row.AvgLatencyMs = (n == 0) ? ms : (row.AvgLatencyMs * n + ms) / (n + 1);
                    row.Samples = n + 1;

                    row.LastComputeAt = DateTime.Now;
                    RefreshGridPreservingSelection();
                });
            };

            // UI hooks for remote QueueWait (unchanged)
            _orchestrator.QueueWaitMeasured += (agentId, app, seq, qms) =>
            {
                OnUI(() =>
                {
                    var row = agentStatuses.FirstOrDefault(a =>
                        a.AgentId.Equals(agentId, StringComparison.OrdinalIgnoreCase));
                    if (row == null) return;

                    row.LastQueueWaitMs = (long)Math.Round(qms);

                    var n = Math.Max(0, row.QueueSamples);
                    row.AvgQueueWaitMs = (n == 0) ? qms : (row.AvgQueueWaitMs * n + qms) / (n + 1);
                    row.QueueSamples = n + 1;

                    RefreshGridPreservingSelection();
                });
            };

            // === NEW: Master-side local metrics (no UI changes; reuse existing columns) ===
            // LocalExecutor lives in NodeCore (headless). It measures queue-wait & "local RTT" (queue+exec).
            if (_localExec == null)
            {
                _localExec = new NodeCore.Exec.LocalExecutor(preferredConcurrency: Math.Min(Environment.ProcessorCount / 2, 4));

                // Feed Master row using the same QueueWait/Latency columns you already have.
                _localExec.MetricsMeasured += (agentId, app, seq, qms, rtt) =>
                {
                    // We publish metrics as agentId="Master" so they land in the Master row
                    OnUI(() =>
                    {
                        var row = agentStatuses.FirstOrDefault(a =>
                            a.AgentId.Equals("Master", StringComparison.OrdinalIgnoreCase));
                        if (row == null) return;

                        // QueueWait (master-local)
                        row.LastQueueWaitMs = (long)Math.Round(qms);
                        var qn = Math.Max(0, row.QueueSamples);
                        row.AvgQueueWaitMs = (qn == 0) ? qms : (row.AvgQueueWaitMs * qn + qms) / (qn + 1);
                        row.QueueSamples = qn + 1;

                        // Latency/RTT (master-local "queue+exec")
                        row.LastLatencyMs = (long)Math.Round(rtt);
                        var ln = Math.Max(0, row.Samples);
                        row.AvgLatencyMs = (ln == 0) ? rtt : (row.AvgLatencyMs * ln + rtt) / (ln + 1);
                        row.Samples = ln + 1;

                        row.LastComputeAt = DateTime.Now;
                        RefreshGridPreservingSelection();
                    });
                };
            }

            // === NEW: DecisionCoordinator (headless, in NodeCore). For now it passes through to the remote orchestrator.
            if (_decider == null)
            {
                _decider = new NodeCore.Orchestration.DecisionCoordinator(
                    () => agentStatuses.ToList(),
                    _orchestrator
                );
            }

            // Note: No behavior change to your existing dispatch paths yet.
            // When you're ready, submit app work via _decider.Submit(app, units)
            // to choose Local vs Remote. For now, everything still goes remote through _orchestrator.
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
                settings.StartAsMaster = (role == NodeRole.Master);
                try { SaveSettings(); } catch { }

                try { System.IO.File.WriteAllText("node.config", role.ToString()); } catch { }

                try { StopMdns(); } catch { }

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

            if (!string.IsNullOrWhiteSpace(agentId) && !agentId.Equals("Master", StringComparison.OrdinalIgnoreCase))
                _lastSeenUtc[agentId] = DateTime.UtcNow;

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

                // Legacy compute path → route to new AppRun fast-path if possible
                if (message.StartsWith("Compute:", StringComparison.OrdinalIgnoreCase))
                {
                    _agentBootstrap?.OnWireMessage("AppRun:" + message.Substring("Compute:".Length));
                    Log($"Routed Compute→AppRun on Agent: {message}");
                    return;
                }

                if (message.StartsWith("CustomTask:", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"CustomTask ignored on Agent (no handler bound): {message}");
                    return;
                }
            }

            if (currentRole == NodeRole.Master &&
                ResultWire.TryParseFromWire(message, agentId, out var env))
            {
                _collator?.Accept(env);
                _orchestrator?.OnResult(env);
                return;
            }

            if (currentRole == NodeRole.Master && message.StartsWith("RegisterAck:", StringComparison.OrdinalIgnoreCase))
            {
                var first = message.IndexOf(':');
                var second = first >= 0 ? message.IndexOf(':', first + 1) : -1;
                string? corr = null;
                if (first >= 0 && second > first)
                {
                    var ackAgentId = message.Substring(first + 1, second - first - 1).Trim();
                    var third = message.IndexOf(':', second + 1);
                    if (third > second) corr = message.Substring(second + 1, third - second - 1).Trim();

                    var already = _registeredAgents.Contains(ackAgentId);
                    if (already)
                    {
                        Log($"Duplicate RegisterAck ignored from {ackAgentId}.");
                        return;
                    }

                    var agentNumber = GetOrAssignAgentNumber(ackAgentId);

                    OnUI(() =>
                    {
                        var row = agentStatuses.FirstOrDefault(a => a.AgentId.Equals(ackAgentId, StringComparison.OrdinalIgnoreCase));
                        if (row == null)
                        {
                            row = new AgentStatus { AgentId = ackAgentId };
                            agentStatuses.Add(row);
                        }
                        row.Registered = true;
                        row.IsOnline = true;
                        row.IsDegraded = false;
                        row.LastHeartbeat = DateTime.Now;
                        _registeredAgents.Add(ackAgentId);
                        RefreshGridPreservingSelection();
                    });

                    // Auto-smoke via new path
                    var payload = "AppRun:QuickSmoke|0|noop";
                    comm.SendToAgent(ackAgentId, payload);
                    Log($"Auto-smoke (AppRun) sent to {ackAgentId} (after RegisterAck).");

                    Log($"RegisterAck accepted from {ackAgentId} (Agent #{agentNumber})");

                    if (_shownRegisterDialog.Add(ackAgentId))
                    {
                        OnUI(() =>
                            MessageBox.Show($"{ackAgentId} registered.",
                                            "Agent Registered", MessageBoxButton.OK, MessageBoxImage.Information));
                    }
                }
                return;
            }

            if (currentRole == NodeRole.Master && message.StartsWith("ComputeTestResult:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var parts = message.Split(':');
                    var aId = parts.Length > 1 ? parts[1].Trim() : agentId;
                    string symbol = "✓";
                    string? word = null;

                    if (parts.Length == 3)
                    {
                        var tok = parts[2].Trim();
                        symbol = (tok.Equals("✓") || tok.Equals("OK", StringComparison.OrdinalIgnoreCase)) ? "✓" : "✗";
                        word = tok;
                    }
                    else if (parts.Length >= 4)
                    {
                        var tok = parts[3].Trim();
                        symbol = tok.Equals("OK", StringComparison.OrdinalIgnoreCase) ? "✓" : "✗";
                        word = tok;
                    }

                    OnUI(() =>
                    {
                        var row = agentStatuses.FirstOrDefault(a => a.AgentId.Equals(aId, StringComparison.OrdinalIgnoreCase));
                        if (row != null)
                        {
                            row.SelfTestStatus = symbol;
                            row.IsOnline = true;
                            row.LastHeartbeat = DateTime.Now;
                            row.LastComputeResult = $"QuickSmoke → {(word ?? symbol)}";
                            row.LastComputeAt = DateTime.Now;
                            row.IsDegraded = !(symbol == "✓");
                            RefreshGridPreservingSelection();
                        }
                    });

                    Log($"Self-test result from {aId}: {symbol}");
                }
                catch (Exception ex)
                {
                    Log($"ComputeTestResult parse error: {ex.Message}");
                }
                return;
            }

            if (currentRole == NodeRole.Master && message.StartsWith("AgentConfig:", StringComparison.OrdinalIgnoreCase))
            {
                var json = message.Substring("AgentConfig:".Length);
                try
                {
                    var cfg = JsonSerializer.Deserialize<NodeCore.AgentConfig>(json);
                    if (cfg != null)
                    {
                        OnUI(() =>
                        {
                            var row = agentStatuses.FirstOrDefault(a => a.AgentId.Equals(cfg.AgentId, StringComparison.OrdinalIgnoreCase))
                                      ?? new AgentStatus { AgentId = cfg.AgentId };

                            row.CpuLogicalCores = cfg.CpuLogicalCores != 0 ? cfg.CpuLogicalCores : row.CpuLogicalCores;
                            row.HasGpu = cfg.HasGpu;
                            row.GpuModel = cfg.GpuModel ?? "";
                            row.GpuMemoryMB = cfg.GpuMemoryMB ?? row.GpuMemoryMB;
                            row.GpuCount = cfg.GpuCount ?? row.GpuCount;
                            row.InstanceId = cfg.InstanceId ?? "";
                            row.IsOnline = true;
                            row.LastHeartbeat = DateTime.Now;

                            UpdateOrAddAgentStatus(row);
                            RefreshGridPreservingSelection();
                        });

                        Log($"Config updated for {cfg.AgentId}");
                    }
                }
                catch (Exception ex) { Log($"Failed to parse AgentConfig: {ex.Message}"); }
                return;
            }

            if (message.StartsWith("AgentPulse:", StringComparison.OrdinalIgnoreCase))
            {
                var json = message.Substring("AgentPulse:".Length);
                try
                {
                    var p = JsonSerializer.Deserialize<NodeCore.AgentPulse>(json);
                    if (p != null)
                    {
                        _lastSeenUtc[p.AgentId] = DateTime.UtcNow;

                        // NEW: inform master orchestrator (clears ban, marks activity)
                        _masterOrchestrator?.NoteAgentPulse(p.AgentId);

                        OnUI(() =>
                        {
                            var row = agentStatuses.FirstOrDefault(a => a.AgentId.Equals(p.AgentId, StringComparison.OrdinalIgnoreCase))
                                      ?? new AgentStatus { AgentId = p.AgentId };

                            float ApplyDelta(float current, float incoming, float minDelta = 1.5f)
                            {
                                if (float.IsNaN(incoming)) return current;
                                if (Math.Abs(current - incoming) < minDelta) return current;
                                return incoming;
                            }

                            row.CpuUsagePercent = ApplyDelta(row.CpuUsagePercent, (float)p.CpuUsagePercent);
                            if (p.MemoryAvailableMB > 0) row.MemoryAvailableMB = p.MemoryAvailableMB;

                            if (!row.HasGpu)
                            {
                                row.GpuUsagePercent = 0f;
                            }
                            else
                            {
                                var g = float.IsNaN(Convert.ToSingle(p.GpuUsagePercent)) ? 0f : p.GpuUsagePercent;
                                if (g >= 0.2f && g <= 100f)
                                    row.GpuUsagePercent = ApplyDelta(row.GpuUsagePercent, g, minDelta: 2.0f);
                            }

                            row.TaskQueueLength = p.TaskQueueLength;
                            row.NetworkMbps = p.NetworkMbps;
                            row.DiskReadMBps = p.DiskReadMBps;
                            row.DiskWriteMBps = p.DiskWriteMBps;
                            row.LastHeartbeat = p.LastHeartbeat == default ? DateTime.Now : p.LastHeartbeat;
                            row.IsOnline = true;

                            UpdateOrAddAgentStatus(row);
                            RefreshGridPreservingSelection();
                        });
                    }
                }
                catch (Exception ex) { Log($"Failed to parse AgentPulse: {ex.Message}"); }
                return;
            }

            if (message.StartsWith("AgentStatus:", StringComparison.OrdinalIgnoreCase))
            {
                var json = message.Substring("AgentStatus:".Length);
                try
                {
                    var status = AgentStatus.FromJson(json, null, null);
                    if (status != null)
                    {
                        _lastSeenUtc[status.AgentId] = DateTime.UtcNow;
                        OnUI(() =>
                        {
                            UpdateOrAddAgentStatus(status);
                            RefreshGridPreservingSelection();
                        });
                        Log($"Updated status for {status.AgentId}");
                    }
                }
                catch (Exception ex) { Log($"Failed to parse AgentStatus: {ex.Message}"); }
                return;
            }

            if (message.StartsWith("ComputeResult:", StringComparison.OrdinalIgnoreCase))
            {
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

                HandleComputeResultMessage(message, agentId);
                return;
            }

            if (message.StartsWith("Result:", StringComparison.OrdinalIgnoreCase))
            {
                Log($"Task response: {message}");
                taskHistory.Add($"{DateTime.Now:T}: {message}");

                HandleComputeResultMessage(message, agentId);
                return;
            }
        }

        private void HandleComputeResultMessage(string payload, string agentId)
        {
            string? job = null;
            string? result = null;

            if (payload.StartsWith("Result:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = payload.Split(':');
                if (parts.Length >= 3) { result = parts[1]; job = parts[2]; }
            }
            else if (payload.StartsWith("ComputeResult:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = payload.Split(':');
                if (parts.Length >= 3) { job = parts[1]; result = parts[2]; }
            }

            bool ok = string.Equals(result, "OK", StringComparison.OrdinalIgnoreCase) || string.Equals(result, "✓", StringComparison.OrdinalIgnoreCase);

            OnUI(() =>
            {
                var row = agentStatuses.FirstOrDefault(a => a.AgentId.Equals(agentId, StringComparison.OrdinalIgnoreCase));
                if (row != null)
                {
                    row.SelfTestStatus = ok ? "✓" : "✗";
                    row.LastComputeResult = (job != null && result != null) ? $"{job} → {result}" : payload;
                    row.LastComputeAt = DateTime.Now;
                    row.IsDegraded = !ok;
                    RefreshGridPreservingSelection();
                }
            });
        }

        private void ReassembleComputeResults(string appName)
        {
            if (computeResults.TryGetValue(appName, out var results))
            {
                var ordered = results.OrderBy(r => r.SequenceId).Select(r => r.Result).ToList();
                Log($"Reassembled results for {appName}: {string.Join(", ", ordered)}");
            }
        }

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
                var selected = NodeGrid.SelectedItems
                    .OfType<AgentStatus>()
                    .Where(a => a.AgentId != "Master")
                    .ToList();

                if (selected.Count > 0)
                {
                    foreach (var a in selected)
                    {
                        var ok = comm.SendToAgent(a.AgentId, "Ping");
                        Log(ok
                            ? $"Pinged {a.AgentId} ({a.IpAddress})"
                            : $"Failed to ping {a.AgentId}");
                    }
                    return;
                }

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

            // Direct send
            if (!comm.SendToAgent(agent.AgentId, payload))
                Log($"Failed to send custom task to {agent.AgentId}");

            Log($"Sent custom task: {task} to {agent.AgentId}");
            taskHistory.Add($"{DateTime.Now:T}: Sent {task} to {agent.AgentId}");
            OnUI(() =>
            {
                tasks.Add(new TaskInfo { TaskId = taskId, Description = task, AgentId = agent.AgentId, Timestamp = DateTime.Now });
                TaskCountLabel.Text = tasks.Count.ToString();
            });
        }

        private void StopTaskMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole != NodeRole.Master) { Log("Stop only in Master mode."); return; }
            if (TaskListBox.SelectedItem is not TaskInfo t) { Log("No task selected."); return; }

            master?.DispatchTaskToAgent($"StopTask:{t.TaskId}", t.AgentId);
            Log($"Requested stop {t.TaskId} on {t.AgentId}");
            taskHistory.Add($"{DateTime.Now:T}: Stop {t.TaskId} on {t.AgentId}");
        }

        private void DispatchTest_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_orchestrator == null || _collator == null)
                {
                    Log("Dispatch test not ready: orchestrator/collator missing.");
                    return;
                }

                // Distributed benchmark using existing orchestrator/collator
                NodeMaster.Distrib.DistributedBenchmark.RunDemoHashBenchmark(
                    _orchestrator,
                    _collator,
                    () => agentStatuses.ToList(),
                    Log,
                    taskCount: 32,
                    bytesPerTask: 2 * 1024 * 1024);
            }
            catch (Exception ex)
            {
                Log($"Dispatch test error: {ex.Message}");
            }
        }

        private async void ComputeTest_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole != NodeRole.Master) { Log("Only in Master mode."); return; }

            if (_orchestrator == null || _collator == null)
            {
                Log("Orchestrator not ready.");
                return;
            }

            var targets = agentStatuses.Where(a => a.IsOnline && a.AgentId != "Master").ToList();
            if (targets.Count == 0) { Log("No online agents to test."); return; }

            var runId = Guid.NewGuid().ToString("N");
            var units = new List<TaskUnit>();
            const string app = "QuickSmoke";

            for (int i = 0; i < 32; i++)
                units.Add(new TaskUnit { App = app, Seq = i, Payload = "noop" });

            _collator.StartJob(runId, app, units.Count);

            _orchestrator.Submit(app, units);

            await Task.Delay(500);
            Log($"ComputeTest orchestrated run submitted: {app} run={runId} units={units.Count}");
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
                StopMdns();
                SaveSettings();

                try
                {
                    heartbeatTimer?.Stop();
                    heartbeatTimer?.Dispose();
                }
                catch { }
            }
            catch { }
            Application.Current.Shutdown();
        }

        private void ApplySettings()
        {
            var ms = Math.Max(1000, settings.HeartbeatIntervalMs);
            if (heartbeatTimer != null)
            {
                heartbeatTimer.Interval = ms;
                heartbeatTimer.AutoReset = true;
            }
            Log($"Heartbeat: {heartbeatTimer.Interval} ms, Master IP: {settings.MasterIp}");
        }

        private void OpenLogsMenu_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = System.IO.Path.GetFullPath(MasterLogPath);
                var dir = System.IO.Path.GetDirectoryName(path);

                if (System.IO.File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{path}\"",
                        UseShellExecute = true
                    });
                }
                else if (!string.IsNullOrEmpty(dir))
                {
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

        // ===================== Heartbeat =====================
        private void SetupHeartbeat()
        {
            heartbeatTimer = new System.Timers.Timer(Math.Max(1000, settings.HeartbeatIntervalMs));
            heartbeatTimer.Elapsed += (s, e) => Dispatcher.Invoke(UpdateHeartbeat);
            heartbeatTimer.AutoReset = true;
            heartbeatTimer.Start();
        }

        private void UpdateHeartbeat()
        {
            if (currentRole == NodeRole.Master)
            {
                var nowUtc = DateTime.UtcNow;

                OnUI(() => UpdateOrAddAgentStatus(BuildLocalStatus("Master")));

                OnUI(() =>
                {
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
                                if (!(NodeGrid?.SelectedItem is AgentStatus sel && sel.AgentId.Equals(a.AgentId, StringComparison.OrdinalIgnoreCase)))
                                    agentStatuses.Remove(a);
                            }
                        }
                        else a.IsOnline = false;
                    }
                });

                MasterUptimeLabel.Text = (DateTime.Now - masterStartTime).ToString(@"hh\:mm\:ss");
                RefreshGridPreservingSelection();

                OnUI(() =>
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

                var local = BuildLocalStatus(id);
                local.CpuUsagePercent = pulse.CpuUsagePercent;
                local.GpuUsagePercent = pulse.GpuUsagePercent;
                local.MemoryAvailableMB = pulse.MemoryAvailableMB;
                local.TaskQueueLength = pulse.TaskQueueLength;
                local.LastHeartbeat = pulse.LastHeartbeat;

                OnUI(() =>
                {
                    UpdateOrAddAgentStatus(local);
                    RefreshGridPreservingSelection();
                });
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

            var localIp = GetLocalIPAddress();
            if (string.Equals(ip, localIp, StringComparison.OrdinalIgnoreCase))
                return;

            if (fqdn.Contains("." + ServiceTypeMaster + ".", StringComparison.OrdinalIgnoreCase))
            {
                settings.MasterIp = ip!;
                SaveSettings();
                OnUI(() =>
                {
                    MasterStatusLabel.Text = "Online";
                    MasterStatusLabel.Foreground = Brushes.Green;
                });
                Log($"Discovered Master at {ip}");
                if (currentRole != NodeRole.Master) comm.SetMasterIp(ip!);
                return;
            }

            if (fqdn.Contains("." + ServiceTypeAgent + ".", StringComparison.OrdinalIgnoreCase))
            {
                var agentId = NormalizeAgentIdFromInstance(fqdn);
                _lastSeenUtc[agentId] = DateTime.UtcNow;
                comm.UpdateAgentEndpoint(agentId, ip!);

                bool alreadyRegistered = false;

                OnUI(() =>
                {
                    var existing = agentStatuses.FirstOrDefault(a => a.AgentId.Equals(agentId, StringComparison.OrdinalIgnoreCase));
                    alreadyRegistered = existing?.Registered == true;

                    var row = existing ?? new AgentStatus { AgentId = agentId };
                    row.IsOnline = true;
                    row.IpAddress = ip!;
                    row.LastHeartbeat = DateTime.Now;

                    UpdateOrAddAgentStatus(row);
                    RefreshGridPreservingSelection();
                });

                if (!_registeredAgents.Contains(agentId) && !alreadyRegistered)
                {
                    var now = DateTime.UtcNow;
                    if (!_lastDiscoveryLog.TryGetValue(agentId, out var last) || (now - last) >= DiscoveryLogMinGap)
                    {
                        Log($"Discovered Agent {agentId} at {ip}");
                        _lastDiscoveryLog[agentId] = now;
                    }
                }

                if (currentRole == NodeRole.Master && ENABLE_AUTO_REGISTER && !_registeredAgents.Contains(agentId) && !alreadyRegistered)
                {
                    var row = agentStatuses.FirstOrDefault(a => a.AgentId.Equals(agentId, StringComparison.OrdinalIgnoreCase));
                    if (row != null)
                    {
                        _ = Task.Run(async () =>
                        {
                            await TryRegisterAgentAsync(row);
                        });
                    }
                }
            }
        }

        private void Mdns_ServiceInstanceShutdown(object? sender, ServiceInstanceShutdownEventArgs e)
        {
            var fqdn = e.ServiceInstanceName.ToString();
            if (string.IsNullOrEmpty(fqdn)) return;

            if (fqdn.Contains("." + ServiceTypeAgent + ".", StringComparison.OrdinalIgnoreCase))
            {
                var agentId = fqdn.Split('.')[0];
                OnUI(() =>
                {
                    var existing = agentStatuses.FirstOrDefault(a => a.AgentId == agentId);
                    if (existing != null && agentId != "Master")
                    {
                        existing.IsOnline = false;
                        RefreshGridPreservingSelection();
                    }
                });
                Log($"Service removed: {agentId}");
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
            agentDiscoveryTimer.Elapsed += (s, e) =>
            {
                var target = _registeredAgents.Count > 0 ? 30000 : 10000;
                if (Math.Abs(agentDiscoveryTimer.Interval - target) > 100)
                    agentDiscoveryTimer.Interval = target;

                DiscoverAgentsAsync().GetAwaiter().GetResult();
            };
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

        private float GetSystemCpuUsagePercent()
        {
            try
            {
                if (_cpuTotalCounter == null)
                {
                    _cpuTotalCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                    _ = _cpuTotalCounter.NextValue();
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

            if (incoming.Registered) existing.Registered = true;
            if (!string.IsNullOrEmpty(incoming.SelfTestStatus))
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

        private void AboutMenu_Click(object sender, RoutedEventArgs e)
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var name = asm.GetName();
            var ver = name.Version?.ToString() ?? "0.0.0.0";
            MessageBox.Show(this, $"NodeLinkUI\nVersion: {ver}", "About", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        public void RefreshCommunicationSecurity() { /* v1-lite: no-op */ }

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

        private sealed class AgentCommAdapter : NodeAgent.INodeComm
        {
            private readonly CommChannel _comm;
            public AgentCommAdapter(CommChannel comm) => _comm = comm;
            public void SendToMaster(string payload) => _comm.SendToMaster(payload);
        }

        private void NodeGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
        private void NodeGrid_SelectionChanged_1(object sender, SelectionChangedEventArgs e) { }

        // ===== Registration helper (HTTP) =====
        private async Task<bool> TryRegisterAgentAsync(AgentStatus sel)
        {
            if (currentRole != NodeRole.Master) return false;
            if (sel == null || string.IsNullOrWhiteSpace(sel.IpAddress))
            {
                Log($"Register skipped: no IP for {sel?.AgentId ?? "unknown"}");
                return false;
            }

            var id = sel.AgentId;

            try
            {
                int port = _cfg.ControlPort;

                var ok = await RegisterAgentHttpAsync(
                    agentId: sel.AgentId,
                    agentIp: sel.IpAddress!,
                    agentPort: port,
                    masterId: "Master",
                    masterIp: GetLocalIPAddress(),
                    bearerToken: _cfg.AuthToken,
                    log: Log);

                if (ok)
                {
                    Log($"Register request sent to {sel.AgentId} at {sel.IpAddress}:{port} via HTTP (awaiting ack) ✅");
                    return true;
                }
                else
                {
                    Log($"Failed to register {sel.AgentId} at {sel.IpAddress}:{port} via HTTP");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"Register error for {id}: {ex.Message}");
                return false;
            }
        }

        private static async Task<bool> RegisterAgentHttpAsync(
            string agentId,
            string agentIp,
            int agentPort,
            string masterId,
            string masterIp,
            string? bearerToken,
            Action<string> log)
        {
            try
            {
                var corr = Guid.NewGuid().ToString("N");
                var url = $"http://{agentIp}:{agentPort}/nl/register?corr={Uri.EscapeDataString(corr)}&master={Uri.EscapeDataString(masterId)}&ip={Uri.EscapeDataString(masterIp)}";

                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                if (!string.IsNullOrWhiteSpace(bearerToken))
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);

                req.Content = new StringContent("");

                using var http = new HttpClient() { Timeout = TimeSpan.FromSeconds(3) };
                var resp = await http.SendAsync(req);
                if ((int)resp.StatusCode == 200)
                {
                    log($"Register POST -> {agentId} OK (corr={corr})");
                    return true;
                }
                else
                {
                    log($"Register POST -> {agentId} HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                log($"Register POST error -> {agentId}: {ex.Message}");
                return false;
            }
        }

        // ====== Missing XAML click handlers (stubs wired to current flow) ======

        // RegisterAgent button in XAML
        private async void RegisterAgent_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole != NodeRole.Master)
            {
                Log("Register is only available in Master mode.");
                return;
            }
            var sel = NodeGrid.SelectedItem as AgentStatus;
            if (sel == null)
            {
                Log("Select an agent row first.");
                return;
            }
            await TryRegisterAgentAsync(sel);
        }

        // StopAllTasksButton in XAML
        private void StopAllTasksButton_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole != NodeRole.Master)
            {
                Log("Stop All is only available in Master mode.");
                return;
            }

            var targets = agentStatuses.Where(a => a.IsOnline && a.AgentId != "Master").ToList();
            if (targets.Count == 0)
            {
                Log("No online agents to stop.");
                return;
            }

            foreach (var a in targets)
                comm.SendToAgent(a.AgentId, "StopAllTasks");

            Log($"Sent StopAllTasks to {targets.Count} agent(s).");
        }

        // UpdateAgentConfig button in XAML
        private void UpdateAgentConfig_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole == NodeRole.Master)
            {
                var sel = NodeGrid.SelectedItem as AgentStatus;
                if (sel == null)
                {
                    Log("Select an agent to request its config.");
                    return;
                }
                comm.SendToAgent(sel.AgentId, "RequestConfig");
                Log($"Requested config from {sel.AgentId}");
            }
            else
            {
                var id = localAgentId ?? $"Agent-{Environment.MachineName}";
                var cfg = BuildAgentConfig(id);
                comm.SendToMaster($"AgentConfig:{JsonSerializer.Serialize(cfg)}");
                Log("Sent AgentConfig to Master.");
            }
        }
    }
}








