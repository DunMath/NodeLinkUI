using NodeComm;
using NodeCore;
using NodeCore.Scheduling;
using NodeMaster;
using NodeAgent;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using Makaretu.Dns;
using NodeLinkUI.Services;

namespace NodeLinkUI
{
    public partial class MainWindow : Window
    {
        // ===================== Constants =====================
        private const string ServiceTypeMaster = "_nodelink._tcp";
        private const string ServiceTypeAgent = "_nodelink-agent._tcp";
        private const string configPath = "node.config";
        private const string settingsPath = "settings.json";

        // ===================== Core state =====================
        private NodeRole currentRole;
        private CommChannel comm;
        private NodeMaster.NodeMaster? master;
        private NodeAgent.NodeAgent? agent;
        private NodeScheduler? scheduler;
        private SchedulerMode selectedMode = SchedulerMode.Auto;

        private List<AgentStatus> agentStatuses = new();
        private List<string> taskHistory = new();
        private ObservableCollection<TaskInfo> tasks = new();

        private System.Timers.Timer heartbeatTimer;
        private DateTime masterStartTime;
        private int agentCount = 0;
        private bool IsProVersion = false; // visual flag only; functional Pro checks use settings.ProMode
        private int taskSequenceId = 0;

        private readonly Dictionary<string, List<(int SequenceId, string Result)>> computeResults = new();
        private NodeLinkSettings settings = new();

        // Per-run instance id (changes every time this process starts)
        private readonly string _thisInstanceId = Guid.NewGuid().ToString();

        // Track last seen agent InstanceId to detect agent restarts (Master side)
        private readonly Dictionary<string, string> _agentInstanceSeen = new(StringComparer.OrdinalIgnoreCase);
        // Tracks which agents are still pending a ComputeTest result for the current run
        private readonly Dictionary<string, string> _computeTestPending = new(StringComparer.OrdinalIgnoreCase);

        // Helpers (new)
        private Plans _plan;
        private PowerCoordinator _power;

        // ===================== Makaretu mDNS =====================
        private MulticastService? mcast;
        private ServiceDiscovery? mdns;
        private ServiceProfile? mdnsMasterProfile;
        private ServiceProfile? mdnsAgentProfile;
        private System.Timers.Timer agentDiscoveryTimer;

        // ===================== Local metrics (CPU & Memory) =====================
        private ulong _prevIdleTicks, _prevKernelTicks, _prevUserTicks;
        private bool _cpuInit;

        // ===================== GPU metrics (v1: single GPU view) =====================
        private List<PerformanceCounter>? _gpuCounters; // "GPU Engine" -> "Utilization Percentage"
        private bool _gpuCountersInit;

        private string? localAgentId;
        // SoftThreads (cache + agent queue)
        private SoftThreadDispatcher? _softDispatcher;
        private AgentWorkQueue? _agentWork;
        private AgentWorkQueue? _agentWorkQueue;

        public MainWindow()
        {
            InitializeComponent();

            currentRole = LoadRoleFromConfig();
            comm = new CommChannel();

            tasks = new ObservableCollection<TaskInfo>();
            TaskListBox.ItemsSource = tasks;

            LoadSettings();

            // Join Code security – seed (Master only) and attach to comms
            SecurityManager.EnsureJoinCode(settings, isMaster: currentRole == NodeRole.Master, log: Log);
            SecurityManager.ApplyToComm(comm, settings);

            // New helpers
            _plan = new Plans(settings, () => agentStatuses);
            _power = new PowerCoordinator(settings, comm, () => localAgentId ?? $"Agent-{Environment.MachineName}", Log);
            _power.WireSystemEvents(this, isAgent: currentRole != NodeRole.Master);

            InitializeRole(currentRole);
            UpdateRoleUI();
            SetupHeartbeat();

            comm.OnMessageReceived += HandleAgentMessage;

            if (currentRole == NodeRole.Master)
            {
                SetupAgentDiscovery();
            }
        }
        // Re-apply the current Join Code to the live comms channel when settings change.
        public void RefreshCommunicationSecurity()
        {
            SecurityManager.ApplyToComm(comm, settings);
        }

        // ===================== Logging & Settings =====================

        private void Log(string message)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (LogBox != null)
                    {
                        LogBox.Items.Add($"{DateTime.Now:T}: {message}");
                        LogBox.ScrollIntoView(LogBox.Items[LogBox.Items.Count - 1]);
                    }
                    else
                    {
                        Console.WriteLine($"LogBox is null: {message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Log error: {ex.Message}");
            }
        }

        private void LoadSettings()
        {
            try
            {
                settings = NodeLinkSettings.Load(settingsPath);
                Log("Settings loaded successfully");
            }
            catch (Exception ex)
            {
                Log($"Failed to load settings: {ex.Message}");
            }
        }

        private void SaveSettings()
        {
            try
            {
                settings.Save(settingsPath);
                Log("Settings saved.");
            }
            catch (Exception ex)
            {
                Log($"Failed to save settings: {ex.Message}");
            }
        }

        // ===================== Role Init =====================

        private async void InitializeRole(NodeRole role)
        {
            RoleLabel.Text = $"Current Role: {role}";
            string localIp = GetLocalIPAddress();

            EnsureMdnsStarted();

            if (role == NodeRole.Master)
            {
                // Advertise Master via Makaretu
                var ip = ParseIp(localIp) ?? IPAddress.Loopback;
                mdnsMasterProfile = new ServiceProfile(
                    "NodeLinkMaster",
                    ServiceTypeMaster,
                    5000,
                    new[] { ip }
                );
                mdns!.Advertise(mdnsMasterProfile);
                Log("mDNS advertised: _nodelink._tcp");

                master = new NodeMaster.NodeMaster(comm);
                masterStartTime = DateTime.Now;

                scheduler = new NodeScheduler(agentStatuses);
                scheduler.SetMode(selectedMode);

                MasterStatusLabel.Text = "Online";
                MasterStatusLabel.Foreground = Brushes.Green;

                // Seed Master row once
                agentStatuses.Add(BuildLocalStatus("Master"));

                NodeGrid.ItemsSource = null;
                NodeGrid.ItemsSource = agentStatuses;
                InitializeSoftThreadsForMaster();
            }
            else
            {
                // Advertise Agent via Makaretu
                localAgentId = $"Agent-{Environment.MachineName}";
                var ip = ParseIp(localIp) ?? IPAddress.Loopback;
                mdnsAgentProfile = new ServiceProfile(
                    $"NodeLinkAgent-{Environment.MachineName}",
                    ServiceTypeAgent,
                    5000,
                    new[] { ip }
                );
                mdns!.Advertise(mdnsAgentProfile);
                Log("mDNS advertised: _nodelink-agent._tcp");

                // Discover Master then fallback
                string masterIp = await DiscoverMasterIpAsync();
                if (string.IsNullOrEmpty(masterIp))
                {
                    masterIp = settings.MasterIp;
                    Log($"mDNS discovery timed out, using saved IP: {masterIp}");
                }
                else
                {
                    settings.MasterIp = masterIp;
                    SaveSettings();
                    Log($"Discovered Master IP: {masterIp}");
                }

                scheduler = null;

                agent = new NodeAgent.NodeAgent(localAgentId, comm);

                // Add Agent to grid using local metrics
                agentStatuses.Add(BuildLocalStatus(localAgentId));

                NodeGrid.ItemsSource = null;
                NodeGrid.ItemsSource = agentStatuses;
                InitializeSoftThreadsForAgent();

                // Apply (Pro-only) power startup policy
                _power.ApplyStartupPolicy(isAgent: true);
            }
        }

        private IPAddress? ParseIp(string ip)
        {
            if (IPAddress.TryParse(ip, out var addr)) return addr;
            return null;
        }

        private string GetLocalIPAddress()
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus == OperationalStatus.Up &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    !ni.Description.ToLower().Contains("virtual") &&
                    !ni.Name.ToLower().Contains("virtual"))
                {
                    foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            return ip.Address.ToString();
                        }
                    }
                }
            }
            return "127.0.0.1";
        }

        private void UpdateRoleUI()
        {
            bool masterOnline = currentRole == NodeRole.Master || IsMasterOnline(settings.MasterIp);
            Dispatcher.Invoke(() =>
            {
                RegisterAgentButton.IsEnabled = currentRole == NodeRole.Master;
                DispatchTaskButton.IsEnabled = currentRole == NodeRole.Master;
                BroadcastButton.IsEnabled = currentRole == NodeRole.Master;
                StopAllTasksButton.IsEnabled = currentRole == NodeRole.Master;

                // Custom task controls exist but may be hidden; safe to set
                TaskInputBox.IsEnabled = currentRole == NodeRole.Master;
                CustomTaskButton.IsEnabled = currentRole == NodeRole.Master;

                NodeGrid.ItemsSource = agentStatuses;
                TaskCountLabel.Text = tasks.Count.ToString();
            });

            if (masterOnline && currentRole != NodeRole.Master)
            {
                MasterStatusLabel.Text = "Online";
                MasterStatusLabel.Foreground = Brushes.Green;
                Log("Master is online.");
            }
            else if (currentRole != NodeRole.Master)
            {
                MasterStatusLabel.Text = "Offline";
                MasterStatusLabel.Foreground = Brushes.Red;
                Log("Master not detected.");
            }
        }

        private bool IsMasterOnline(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip) || ip == "Unknown") return false;
            try
            {
                using var ping = new Ping();
                return ping.Send(ip, 1000).Status == IPStatus.Success;
            }
            catch
            {
                return false;
            }
        }

        private NodeRole LoadRoleFromConfig()
        {
            if (!File.Exists(configPath)) return NodeRole.Agent;
            var roleText = File.ReadAllText(configPath).Trim();
            return Enum.TryParse(roleText, out NodeRole role) ? role : NodeRole.Agent;
        }

        private void SaveRoleToConfig(NodeRole role)
        {
            File.WriteAllText(configPath, role.ToString());
        }

        // ===================== Buttons & UI =====================

        private void SwitchRole_Click(object sender, RoutedEventArgs e)
        {
            var newRole = currentRole == NodeRole.Master ? NodeRole.Agent : NodeRole.Master;
            var result = MessageBox.Show(
                $"Switch to {newRole} and restart the app?",
                "Confirm Role Switch",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                SaveRoleToConfig(newRole);
                Log($"Switched to {newRole}");
                RestartApp(newRole);
            }
            else
            {
                Log("Role switch cancelled.");
            }
        }

        private void RestartApp(NodeRole newRole)
        {
            try
            {
                DisposeSoftThreads();
                StopMdns();

                if (agentDiscoveryTimer != null)
                {
                    agentDiscoveryTimer.Stop();
                    agentDiscoveryTimer.Dispose();
                }

                File.AppendAllText("restart.log", $"Restart triggered at {DateTime.Now:yyyy-MM-dd HH:mm:ss} for role: {newRole}\n");
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    System.Diagnostics.Process.Start(exePath);
                }
                else
                {
                    Log("Failed to locate executable path for restart.");
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to restart app: {ex.Message}");
            }
            Application.Current.Shutdown();
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                DisposeSoftThreads();
                StopMdns();

                if (agentDiscoveryTimer != null)
                {
                    agentDiscoveryTimer.Stop();
                    agentDiscoveryTimer.Dispose();
                }

                SaveSettings();
                File.AppendAllText("restart.log", $"App exited at {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                Log($"Failed to exit cleanly: {ex.Message}");
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow(settings, agentStatuses, IsProVersion);
            settingsWindow.Owner = this;
            settingsWindow.ShowDialog();
            ApplySettings();
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
                else
                {
                    Log($"Invalid scheduler mode: {modeText}");
                }
            }
        }
        // --- Add inside the MainWindow class (anywhere among your other event handlers) ---
        #region Help / About menu

        private void HelpMenu_Click(object sender, RoutedEventArgs e)
        {
            // Try local help first
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
        Path.Combine(baseDir, "help", "index.html"),
        Path.Combine(baseDir, "help", "help.html"),
        Path.Combine(baseDir, "help.pdf"),
    };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    TryShellOpen(path);
                    return;
                }
            }

            // Fallback to the project README (adjust to your docs URL if needed)
            TryShellOpen("https://github.com/DunMath/NodeLinkUI#readme");
        }

        private void AboutMenu_Click(object sender, RoutedEventArgs e)
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var name = asm.GetName();
            var ver = name.Version?.ToString() ?? "0.0.0.0";
            var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(infoVer))
                ver = infoVer;

            string roleText = currentRole.ToString();
            string plan = (settings?.ProMode == true) ? "Pro" : "Free";
            string build = Environment.Is64BitProcess ? "x64" : "x86";

            string msg =
                "SoftThreader / NodeLinkUI\n" +
                $"Version: {ver}\n" +
                $"Role: {roleText}   Plan: {plan}\n" +
                $"Machine: {Environment.MachineName}\n" +
                $".NET: {Environment.Version}   {build}\n\n" +
                $"© {DateTime.Now:yyyy} DunMath. All rights reserved.";

            MessageBox.Show(this, msg, "About", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static void TryShellOpen(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open:\n{target}\n\n{ex.Message}",
                    "Open failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        #endregion

        // ===================== Messaging & Tasks =====================

        private void HandleAgentMessage(string agentId, string message)
        {
            Log($"Message from {agentId}: {message}");

            // ===================== Agent-side fast paths =====================
            // Let PowerCoordinator consume Pro-only messages first (existing behavior)
            if (currentRole != NodeRole.Master && _power.TryHandleMessage(message, isAgent: true))
                return;

            // ComputeTest: exercise the real dispatch/return path (Agent replies to Master)
            if (currentRole == NodeRole.Agent && message.StartsWith("ComputeTest:", StringComparison.OrdinalIgnoreCase))
            {
                var testId = message.Substring("ComputeTest:".Length).Trim();
                var thisId = localAgentId ?? $"Agent-{Environment.MachineName}";
                try
                {
                    // Do a tiny bit of work so it's not just an echo
                    long acc = 0;
                    for (int i = 1; i <= 50; i++) acc += i;

                    comm.SendToMaster($"ComputeTestResult:{thisId}:{testId}:OK");
                }
                catch
                {
                    comm.SendToMaster($"ComputeTestResult:{thisId}:{testId}:FAIL");
                }
                return;
            }

            // Agent: buffer incoming compute/custom work so the UI thread isn't blocked
            if (currentRole == NodeRole.Agent &&
                (message.StartsWith("Compute:", StringComparison.OrdinalIgnoreCase) ||
                 message.StartsWith("CustomTask:", StringComparison.OrdinalIgnoreCase)))
            {
                _agentWork?.Enqueue(message);
                return;
            }

            // ===================== Master-side: structured messages =====================

            // Master receives: ComputeTestResult:{agentId}:{testId}:{OK|FAIL}
            if (currentRole == NodeRole.Master && message.StartsWith("ComputeTestResult:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = message.Split(':');
                if (parts.Length >= 4)
                {
                    var agent = parts[1];
                    var testId = parts[2];
                    var outcome = parts[3];

                    var status = agentStatuses.FirstOrDefault(s => s.AgentId.Equals(agent, StringComparison.OrdinalIgnoreCase));
                    if (status != null)
                    {
                        bool accept;
                        lock (_computeTestPending)
                        {
                            accept = _computeTestPending.TryGetValue(agent, out var pending) && pending == testId;
                            if (accept) _computeTestPending.Remove(agent);
                        }

                        // Only update if this result matches the current run
                        if (accept)
                        {
                            status.SelfTestStatus = outcome.Equals("OK", StringComparison.OrdinalIgnoreCase) ? "✓" : "✗";

                            Dispatcher.Invoke(() =>
                            {
                                NodeGrid.ItemsSource = null;
                                NodeGrid.ItemsSource = agentStatuses;
                            });
                        }
                    }
                }
                return;
            }

            // Master/Agent: status updates
            if (message.StartsWith("AgentStatus:", StringComparison.OrdinalIgnoreCase))
            {
                var json = message.Substring("AgentStatus:".Length);
                try
                {
                    var status = AgentStatus.FromJson(json);

                    // Pro upsell trigger on Master: on each Agent restart if that Agent has >1 GPU
                    if (currentRole == NodeRole.Master && status != null && status.GpuCount > 1)
                    {
                        var seen = _agentInstanceSeen.TryGetValue(status.AgentId, out var lastId) ? lastId : null;
                        if (string.IsNullOrEmpty(seen) || !string.Equals(seen, status.InstanceId ?? "", StringComparison.OrdinalIgnoreCase))
                        {
                            _agentInstanceSeen[status.AgentId] = status.InstanceId ?? "";
                            Dispatcher.Invoke(() =>
                            {
                                AlertBox.Items.Add($"Multiple GPUs detected on {status.AgentId}. Upgrade to Pro Version.");
                            });
                        }
                        else
                        {
                            _agentInstanceSeen[status.AgentId] = status.InstanceId ?? "";
                        }
                    }

                    // Track first-seen order (Master only)
                    if (currentRole == NodeRole.Master && status != null)
                        _plan.TouchFirstSeen(status.AgentId);

                    var existing = agentStatuses.FirstOrDefault(a => a.AgentId == status.AgentId);
                    if (existing != null) agentStatuses.Remove(existing);
                    agentStatuses.Add(status);

                    Dispatcher.Invoke(() =>
                    {
                        NodeGrid.ItemsSource = null;
                        NodeGrid.ItemsSource = agentStatuses;
                        if (currentRole == NodeRole.Agent && agentStatuses.Any(s => s.AgentId == "Master"))
                        {
                            MasterStatusLabel.Text = "Online";
                            MasterStatusLabel.Foreground = Brushes.Green;
                        }
                    });

                    // Keep SoftThreads summary in sync on Master
                    if (currentRole == NodeRole.Master)
                        UpdateSoftThreadStats();

                    Log($"Updated status for {status.AgentId}");
                }
                catch (Exception ex)
                {
                    Log($"Failed to parse AgentStatus: {ex.Message}");
                }
                return;
            }

            // Master/Agent: compute result stream (existing)
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

                // Tell the dispatcher one unit completed (for pacing) and refresh stats
                _softDispatcher?.OnResultReceived(agentId);
                UpdateSoftThreadStats();
                return;
            }

            // Master/Agent: ad-hoc custom task bookkeeping (existing)
            if (message.StartsWith("CustomTask:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = message.Split(':');
                if (parts.Length > 2)
                {
                    var taskId = parts[1];
                    var description = string.Join(":", parts, 2, parts.Length - 2);

                    // Only allow tasks to in-plan agents (Master side only generates these, but keep safe)
                    if (currentRole == NodeRole.Master && !_plan.IsWithinPlan(agentId))
                    {
                        Log($"Custom task ignored for {agentId} (over free plan limit).");
                        Dispatcher.Invoke(() => AlertBox.Items.Add($"{agentId} is over the Free plan limit and won’t receive tasks. Upgrade to Pro."));
                        return;
                    }

                    Dispatcher.Invoke(() =>
                    {
                        tasks.Add(new TaskInfo
                        {
                            TaskId = taskId,
                            Description = description,
                            AgentId = agentId,
                            Timestamp = DateTime.Now
                        });
                        taskHistory.Add($"{DateTime.Now:T}: {description} on {agentId}");
                        TaskCountLabel.Text = tasks.Count.ToString();
                    });
                    Log($"Received custom task: {description} for Agent: {agentId}");
                }
                return;
            }

            // Master/Agent: stop single task (existing)
            if (message.StartsWith("StopTask:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = message.Split(':');
                if (parts.Length > 1)
                {
                    var taskId = parts[1];
                    Dispatcher.Invoke(() =>
                    {
                        var task = tasks.FirstOrDefault(t => t.TaskId == taskId && t.AgentId == agentId);
                        if (task != null)
                        {
                            tasks.Remove(task);
                            taskHistory.Add($"{DateTime.Now:T}: Stopped task {taskId} on {agentId}");
                            TaskCountLabel.Text = tasks.Count.ToString();
                        }
                    });
                    Log($"Stopped task {taskId} on Agent: {agentId}");
                }
                return;
            }

            // Master/Agent: stop all (existing)
            if (string.Equals(message, "StopAllTasks", StringComparison.OrdinalIgnoreCase))
            {
                Dispatcher.Invoke(() =>
                {
                    tasks.Clear();
                    taskHistory.Add($"{DateTime.Now:T}: Stopped all tasks");
                    TaskCountLabel.Text = "0";
                });
                Log("Stopped all tasks");
                return;
            }

            // Master/Agent: miscellaneous launch/error results (existing)
            if (message.StartsWith("Result:", StringComparison.OrdinalIgnoreCase) ||
                message.StartsWith("Launched:", StringComparison.OrdinalIgnoreCase) ||
                message.StartsWith("Launch failed:", StringComparison.OrdinalIgnoreCase))
            {
                Log($"Task response: {message}");
                taskHistory.Add($"{DateTime.Now:T}: {message}");

                // Generic completion feedback for the dispatcher pacing
                _softDispatcher?.OnResultReceived(agentId);
                UpdateSoftThreadStats();
                return;
            }
        }



        private void Broadcast_Click(object sender, RoutedEventArgs e)
        {
            string message = $"Ping-{DateTime.Now.Ticks}";
            comm.Broadcast(message);
            Log($"Broadcasted: {message}");
        }

        private void CustomTaskButton_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole == NodeRole.Master)
            {
                string task = TaskInputBox.Text;
                if (!string.IsNullOrEmpty(task))
                {
                    var agent = agentStatuses
                        .FirstOrDefault(a => a.IsOnline && a.AgentId != "Master" && _plan.IsWithinPlan(a.AgentId));

                    if (agent != null)
                    {
                        string taskId = Guid.NewGuid().ToString();
                        master?.DispatchTaskToAgent($"CustomTask:{taskId}:{task}", agent.AgentId);
                        Log($"Sent custom task: {task} to Agent: {agent.AgentId}");
                        taskHistory.Add($"{DateTime.Now:T}: Sent {task} to {agent.AgentId}");
                        tasks.Add(new TaskInfo
                        {
                            TaskId = taskId,
                            Description = task,
                            AgentId = agent.AgentId,
                            Timestamp = DateTime.Now
                        });
                        TaskCountLabel.Text = tasks.Count.ToString();
                    }
                    else
                    {
                        Log("No eligible agents (within plan) to assign task");
                        Dispatcher.Invoke(() => AlertBox.Items.Add("No eligible agents within Free plan. Upgrade to Pro for more."));
                    }
                }
                else
                {
                    Log("No custom task entered");
                }
            }
            else
            {
                Log("Custom tasks can only be sent in Master mode");
            }
        }

        private void StopTaskMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole == NodeRole.Master)
            {
                var selectedTask = TaskListBox.SelectedItem as TaskInfo;
                if (selectedTask != null)
                {
                    master?.DispatchTaskToAgent($"StopTask:{selectedTask.TaskId}", selectedTask.AgentId);
                    Log($"Requested to stop task {selectedTask.TaskId} on Agent: {selectedTask.AgentId}");
                    taskHistory.Add($"{DateTime.Now:T}: Requested to stop task {selectedTask.TaskId} on {selectedTask.AgentId}");
                }
                else
                {
                    Log("No task selected");
                }
            }
            else
            {
                Log("Tasks can only be stopped in Master mode");
            }
        }

        private void StopAllTasksButton_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole != NodeRole.Master) { Log("Tasks can only be stopped in Master mode"); return; }

            // Only send to agents within plan (out-of-plan agents shouldn’t be running tasks, but safe anyway)
            var targetAgents = agentStatuses.Where(a => a.AgentId != "Master" && a.IsOnline && _plan.IsWithinPlan(a.AgentId)).Select(a => a.AgentId).ToList();
            if (targetAgents.Count == 0)
            {
                Log("No eligible agents within plan to stop tasks.");
                return;
            }

            foreach (var id in targetAgents)
                master?.DispatchTaskToAgent("StopAllTasks", id);

            Log("Requested to stop all tasks on eligible agents");
            taskHistory.Add($"{DateTime.Now:T}: Requested to stop all tasks (eligible agents only)");
        }

        private void RegisterAgent_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole != NodeRole.Master || master is null) return;

            var agentId = $"Agent-{++agentCount}";
            master.RegisterAgent(agentId, message => HandleAgentMessage(agentId, message));

            var status = BuildLocalStatus(agentId);

            agentStatuses.Add(status);
            _plan.TouchFirstSeen(agentId);

            Dispatcher.Invoke(() =>
            {
                NodeGrid.ItemsSource = null;
                NodeGrid.ItemsSource = agentStatuses;
            });
            Log($"Registered {agentId}");
        }

        private void DispatchTask_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole != NodeRole.Master || master is null) return;

            if (settings.NominatedApps.Count == 0)
            {
                Log("No nominated apps available for dispatch.");
                return;
            }

            foreach (var app in settings.NominatedApps)
            {
                DispatchComputeTasksForApp(app);
            }
        }
        private async void ComputeTest_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole != NodeRole.Master)
            {
                Log("Compute Test is only available in Master mode.");
                return;
            }

            // Snapshot the agents (skip the Master row)
            var targets = agentStatuses
                .Where(a => a != null && a.IsOnline && !string.Equals(a.AgentId, "Master", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (targets.Count == 0)
            {
                Log("No online agents to test.");
                return;
            }

            // One run id for all agents this click; results carry back (agentId + testId)
            var runId = Guid.NewGuid().ToString("N");

            // Mark pending + fire the test to each agent
            foreach (var a in targets)
            {
                a.SelfTestStatus = "…";
                lock (_computeTestPending)
                    _computeTestPending[a.AgentId] = runId;

                // Send through the real compute pipe
                if (master != null)
                    master.DispatchTaskToAgent($"ComputeTest:{runId}", a.AgentId);
                else
                    comm.SendToAgent(a.AgentId, $"ComputeTest:{runId}");
            }

            // Refresh UI to show pending
            Dispatcher.Invoke(() =>
            {
                NodeGrid.ItemsSource = null;
                NodeGrid.ItemsSource = agentStatuses;
            });

            // Timeout: any agent that hasn't replied in time is marked as fail
            await Task.Delay(3000); // 3s window for replies

            foreach (var a in targets)
            {
                bool stillPending;
                lock (_computeTestPending)
                {
                    stillPending = _computeTestPending.TryGetValue(a.AgentId, out var id) && id == runId;
                    if (stillPending) _computeTestPending.Remove(a.AgentId);
                }
                if (stillPending)
                    a.SelfTestStatus = "✗";
            }

            // Final UI refresh
            Dispatcher.Invoke(() =>
            {
                NodeGrid.ItemsSource = null;
                NodeGrid.ItemsSource = agentStatuses;
            });
        }

        private void DispatchComputeTasksForApp(NominatedApp app)
        {
            bool preferGpu = settings.UseGpuGlobally;

            var eligibleAgents = agentStatuses
                .Where(a => a.IsOnline && a.AgentId != "Master" && (!preferGpu || a.HasGpu))
                .Where(a => _plan.IsWithinPlan(a.AgentId)) // enforce Free plan cap
                .ToList();

            if (!eligibleAgents.Any())
            {
                Log($"No suitable agents available for {app.Name} (Free plan cap or GPU requirement).");
                Dispatcher.Invoke(() => AlertBox.Items.Add($"No eligible agents (within Free plan) for {app.Name}. Upgrade to Pro for more parallelism."));
                return;
            }

            for (int i = 0; i < 10; i++)
            {
                scheduler = new NodeScheduler(eligibleAgents);
                scheduler.SetMode(selectedMode);

                string taskId = $"task_{taskSequenceId}";
                string taskPayload = $"Compute:{app.Name}|{taskSequenceId}|task_{i}";
                string selectedAgentId = scheduler.SelectAgent(taskPayload, null);

                if (string.IsNullOrEmpty(selectedAgentId) || selectedAgentId == "None")
                {
                    Log($"No suitable agents for compute task {app.Name} (task {i})");
                    continue;
                }

                // Queue via SoftThreads dispatcher if available; otherwise send directly
                if (_softDispatcher != null)
                {
                    _softDispatcher.Enqueue(selectedAgentId, taskId, taskPayload);
                    Log($"Queued SoftThread for {app.Name} (task {i}) to {selectedAgentId}");
                }
                else
                {
                    master?.DispatchTaskToAgent(taskPayload, selectedAgentId);
                    Log($"Dispatched compute task {app.Name} (task {i}) to {selectedAgentId}");
                }

                taskHistory.Add(taskPayload);

                Dispatcher.Invoke(() =>
                {
                    tasks.Add(new TaskInfo
                    {
                        TaskId = taskId,
                        Description = taskPayload,
                        AgentId = selectedAgentId,
                        Timestamp = DateTime.Now
                    });

                    // If this label exists in your XAML, keep it updated.
                    try { TaskCountLabel.Text = tasks.Count.ToString(); } catch { /* ignore if not present */ }
                });

                taskSequenceId++;
            }

            // Keep the SoftThreads summary fresh (no-op on Agent / if controls missing)
            UpdateSoftThreadStats();
        }


        private void ReassembleComputeResults(string appName)
        {
            if (computeResults.TryGetValue(appName, out var results))
            {
                var orderedResults = results.OrderBy(r => r.SequenceId).Select(r => r.Result).ToList();
                Log($"Reassembled results for {appName}: {string.Join(", ", orderedResults)}");
            }
        }

        // ===================== Heartbeat =====================

        private void SetupHeartbeat()
        {
            heartbeatTimer = new System.Timers.Timer(settings.HeartbeatIntervalMs);
            heartbeatTimer.Elapsed += (s, e) => Dispatcher.Invoke(UpdateHeartbeat);
            heartbeatTimer.AutoReset = true;
            heartbeatTimer.Start();
        }

        private void ApplySettings()
        {
            Log($"Heartbeat Interval: {settings.HeartbeatIntervalMs} ms");
            Log($"Use GPU Globally: {settings.UseGpuGlobally}");
            heartbeatTimer.Interval = settings.HeartbeatIntervalMs;
        }

        private void UpdateHeartbeat()
        {
            if (currentRole == NodeRole.Master)
            {
                // Build fresh list: Master self + agents from NodeMaster
                var statuses = master != null ? master.GetAgentStatuses() : new List<AgentStatus>();

                // Ensure Master row at the top with current local metrics
                var masterStatus = BuildLocalStatus("Master");
                statuses.Insert(0, masterStatus);
                agentStatuses = statuses;

                // Record first-seen for any new agents
                foreach (var a in agentStatuses.Where(x => x.AgentId != "Master"))
                    _plan.TouchFirstSeen(a.AgentId);

                MasterUptimeLabel.Text = (DateTime.Now - masterStartTime).ToString(@"hh\:mm\:ss");

                Dispatcher.Invoke(() =>
                {
                    NodeGrid.ItemsSource = null;
                    NodeGrid.ItemsSource = agentStatuses;

                    AlertBox.Items.Clear();
                    foreach (var status in agentStatuses)
                    {
                        if (status.CpuUsagePercent > 85)
                            AlertBox.Items.Add($"{status.AgentId} high CPU: {status.CpuUsagePercent:F0}%");
                        if (!status.IsOnline)
                            AlertBox.Items.Add($"{status.AgentId} is offline");
                        if (status.HasGpu)
                            AlertBox.Items.Add($"{status.AgentId} GPU: {status.GpuModel} ({status.GpuMemoryMB}MB)");
                        else
                            AlertBox.Items.Add($"{status.AgentId} has no GPU");
                    }

                    // Free plan alerts
                    _plan.AddOverCapAlerts(AlertBox);
                });
            }
            else
            {
                // Agent heartbeat with local metrics
                var id = localAgentId ?? $"Agent-{Environment.MachineName}";
                var status = BuildLocalStatus(id);
                status.TaskQueueLength = tasks.Count(t => t.AgentId == id);

                comm.SendToMaster($"AgentStatus:{status.ToJson()}");
                Log($"Heartbeat sent from agent at {DateTime.Now:T}");

                // Also update our local grid row
                var existing = agentStatuses.FirstOrDefault(a => a.AgentId == id);
                if (existing != null) agentStatuses.Remove(existing);
                agentStatuses.Add(status);
                Dispatcher.Invoke(() =>
                {
                    NodeGrid.ItemsSource = null;
                    NodeGrid.ItemsSource = agentStatuses;
                    UpdateSoftThreadStats();
                });

                // Power (Pro-only)
                bool busy = tasks.Any(t => t.AgentId == id);
                _power.OnAgentHeartbeat(busy);
                _power.CheckPowerAction();
            }
        }

        // ===================== mDNS (Makaretu) =====================

        private void EnsureMdnsStarted()
        {
            if (mcast == null)
            {
                mcast = new MulticastService();
                mcast.Start();
            }

            if (mdns == null)
            {
                mdns = new ServiceDiscovery(mcast);

                mdns.ServiceDiscovered += (s, serviceName) =>
                {
                    try { mdns.QueryServiceInstances(serviceName); } catch { /* ignore */ }
                };

                mdns.ServiceInstanceDiscovered += Mdns_ServiceInstanceDiscovered;
                mdns.ServiceInstanceShutdown += Mdns_ServiceInstanceShutdown;
            }

            try { mdns.QueryAllServices(); } catch { /* ignore */ }
        }

        private void Mdns_ServiceInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
        {
            var fqdn = e.ServiceInstanceName?.ToString() ?? "";
            var ip = TryGetIp(e);

            if (string.IsNullOrEmpty(fqdn) || string.IsNullOrEmpty(ip))
                return;

            // MASTER service: cache Master IP
            if (fqdn.Contains("." + ServiceTypeMaster + ".", StringComparison.OrdinalIgnoreCase))
            {
                settings.MasterIp = ip!;
                SaveSettings();

                Dispatcher.Invoke(() =>
                {
                    MasterStatusLabel.Text = "Online";
                    MasterStatusLabel.Foreground = Brushes.Green;
                });

                Log($"Discovered Master at {ip}");
                return;
            }

            // AGENT service: normalize instance -> "Agent-HOST"
            if (fqdn.Contains("." + ServiceTypeAgent + ".", StringComparison.OrdinalIgnoreCase))
            {
                var normalizedId = NormalizeAgentIdFromInstance(fqdn);
                var existing = agentStatuses.FirstOrDefault(a => a.AgentId.Equals(normalizedId, StringComparison.OrdinalIgnoreCase));

                // Ensure NodeMaster is tracking this Agent ID (canonical)
                if (existing == null && !normalizedId.Equals("Master", StringComparison.OrdinalIgnoreCase))
                {
                    master?.RegisterAgent(normalizedId, msg => HandleAgentMessage(normalizedId, msg));

                    agentStatuses.Add(new AgentStatus
                    {
                        AgentId = normalizedId,
                        IsOnline = true,
                        IpAddress = ip!,
                        CpuUsagePercent = 0f,
                        GpuUsagePercent = 0f,
                        MemoryAvailableMB = 0f,
                        NetworkMbps = 0f,
                        HasFileAccess = true,
                        AvailableFiles = Array.Empty<string>(),
                        TaskQueueLength = 0,
                        LastHeartbeat = DateTime.Now
                    });

                    _plan.TouchFirstSeen(normalizedId);

                    Dispatcher.Invoke(() =>
                    {
                        NodeGrid.ItemsSource = null;
                        NodeGrid.ItemsSource = agentStatuses;
                    });

                    Log($"Discovered Agent {normalizedId} at {ip}");
                }
                else if (existing != null)
                {
                    existing.IsOnline = true;
                    existing.IpAddress = ip!;
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
                var existing = agentStatuses.FirstOrDefault(a => a.AgentId == agentId);
                if (existing != null && agentId != "Master")
                {
                    existing.IsOnline = false;
                    Dispatcher.Invoke(() =>
                    {
                        NodeGrid.ItemsSource = null;
                        NodeGrid.ItemsSource = agentStatuses;
                    });
                    Log($"Service removed: {agentId}");
                }
            }
        }

        private static string? TryGetIp(ServiceInstanceDiscoveryEventArgs e)
        {
            var msg = e.Message;
            if (msg == null) return null;

            var srv = msg.AdditionalRecords.OfType<SRVRecord>().FirstOrDefault();
            if (srv == null) return null;

            var a = msg.AdditionalRecords.OfType<ARecord>().FirstOrDefault(r => r.Name == srv.Target);
            if (a != null) return a.Address.ToString();

            var aaaa = msg.AdditionalRecords.OfType<AAAARecord>().FirstOrDefault(r => r.Name == srv.Target);
            return aaaa?.Address.ToString();
        }

        private async Task<string> DiscoverMasterIpAsync()
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
            catch (Exception ex)
            {
                Log($"mDNS Master discovery error: {ex.Message}");
            }
            return null;
        }

        private async Task DiscoverAgentsAsync()
        {
            try
            {
                EnsureMdnsStarted();
                mdns!.QueryServiceInstances(ServiceTypeAgent);
                await Task.Delay(200); // brief wait for callbacks
            }
            catch (Exception ex)
            {
                Log($"mDNS Agent discovery error: {ex.Message}");
            }
        }

        private void SetupAgentDiscovery()
        {
            agentDiscoveryTimer = new System.Timers.Timer(10000); // every 10s
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
            catch { /* ignore */ }

            mcast = null;
            mdns = null;
            mdnsMasterProfile = null;
            mdnsAgentProfile = null;
        }

        // ===================== Local system metrics helpers =====================

        private float GetSystemCpuUsagePercent()
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
                return 0f;

            ulong idleTicks = ((ulong)idle.dwHighDateTime << 32) + (uint)idle.dwLowDateTime;
            ulong kernelTicks = ((ulong)kernel.dwHighDateTime << 32) + (uint)kernel.dwLowDateTime;
            ulong userTicks = ((ulong)user.dwHighDateTime << 32) + (uint)user.dwLowDateTime;

            if (!_cpuInit)
            {
                _prevIdleTicks = idleTicks;
                _prevKernelTicks = kernelTicks;
                _prevUserTicks = userTicks;
                _cpuInit = true;
                return 0f; // first call has no delta
            }

            ulong idleDelta = idleTicks - _prevIdleTicks;
            ulong kernelDelta = kernelTicks - _prevKernelTicks;
            ulong userDelta = userTicks - _prevUserTicks;

            _prevIdleTicks = idleTicks;
            _prevKernelTicks = kernelTicks;
            _prevUserTicks = userTicks;

            ulong total = kernelDelta + userDelta;
            if (total == 0) return 0f;

            float busy = (float)(total - idleDelta) / total * 100f;
            if (busy < 0f) busy = 0f;
            if (busy > 100f) busy = 100f;
            return busy;
        }

        private float GetAvailableMemoryMB()
        {
            var msex = new MEMORYSTATUSEX();
            msex.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (GlobalMemoryStatusEx(ref msex))
            {
                return (float)(msex.ullAvailPhys / (1024.0 * 1024.0));
            }
            return 0f;
        }

        private (bool hasGpu, string model, int vramMB) GetPrimaryGpuInfo()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, AdapterRAM, PNPDeviceID FROM Win32_VideoController");
                foreach (ManagementObject mo in searcher.Get())
                {
                    string pnp = mo["PNPDeviceID"]?.ToString() ?? "";
                    if (!pnp.Contains("PCI", StringComparison.OrdinalIgnoreCase))
                        continue; // prefer PCI devices

                    string model = mo["Name"]?.ToString() ?? "GPU";
                    long bytes = 0;
                    var ramObj = mo["AdapterRAM"];
                    if (ramObj != null)
                    {
                        try { bytes = Convert.ToInt64(ramObj); } catch { bytes = 0; }
                    }
                    int mb = (int)Math.Round(bytes / (1024.0 * 1024.0));
                    return (mb > 0, model, mb);
                }

                // Fallback: first controller
                using var searcher2 = new ManagementObjectSearcher(
                    "SELECT Name, AdapterRAM FROM Win32_VideoController");
                var first = searcher2.Get().Cast<ManagementObject>().FirstOrDefault();
                if (first != null)
                {
                    string model = first["Name"]?.ToString() ?? "GPU";
                    long bytes = 0;
                    var ramObj = first["AdapterRAM"];
                    if (ramObj != null)
                    {
                        try { bytes = Convert.ToInt64(ramObj); } catch { bytes = 0; }
                    }
                    int mb = (int)Math.Round(bytes / (1024.0 * 1024.0));
                    return (mb > 0, model, mb);
                }
            }
            catch { /* ignore */ }

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

                foreach (var c in _gpuCounters) _ = c.NextValue(); // prime
            }
            catch
            {
                _gpuCounters = null;
            }
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
                    try { sum += c.NextValue(); } catch { /* ignore this counter */ }
                }
                if (sum < 0f) sum = 0f;
                if (sum > 100f) sum = 100f; // cap
                return sum;
            }
            catch { return 0f; }
        }

        private int GetGpuCountLocal()
        {
            try
            {
                using var s = new ManagementObjectSearcher(
                    "SELECT Name, PNPDeviceID FROM Win32_VideoController");
                int count = 0;
                foreach (ManagementObject mo in s.Get())
                {
                    string name = mo["Name"]?.ToString() ?? "";
                    string pnp = mo["PNPDeviceID"]?.ToString() ?? "";
                    if (pnp.IndexOf("PCI", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        name.IndexOf("Microsoft Basic Display", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        count++;
                    }
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

                // v1 single-GPU fields
                HasGpu = hasGpu,
                HasCuda = false,
                GpuModel = gpuModel,
                GpuMemoryMB = vramMB,

                // For Pro upsell on Master (Agent restarts + multi-GPU detection)
                GpuCount = GetGpuCountLocal(),
                InstanceId = _thisInstanceId,

                LastTaskDuration = TimeSpan.Zero,
                DiskReadMBps = 0f,
                DiskWriteMBps = 0f
            };
        }

        // P/Invoke: CPU times
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        // P/Invoke: Memory status
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
        private static string NormalizeAgentIdFromInstance(string? instanceName)
        {
            if (string.IsNullOrWhiteSpace(instanceName))
                return "Agent-Unknown";

            // Take leftmost label if a full FQDN was provided.
            var left = instanceName;
            var dot = left.IndexOf('.');
            if (dot > 0) left = left.Substring(0, dot);

            const string mdnsPrefix = "NodeLinkAgent-";
            if (left.StartsWith(mdnsPrefix, System.StringComparison.OrdinalIgnoreCase))
                left = "Agent-" + left.Substring(mdnsPrefix.Length);

            // Ensure canonical prefix even if something else slips through
            if (!left.StartsWith("Agent-", System.StringComparison.OrdinalIgnoreCase))
                left = "Agent-" + left;

            return left;
        }
    }
}





