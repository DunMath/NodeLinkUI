using NodeComm;
using NodeCore;
using NodeCore.Scheduling;
using NodeMaster;
using NodeAgent;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Zeroconf;

namespace NodeLinkUI
{
    public partial class MainWindow : Window
    {
        private NodeRole currentRole;
        private CommChannel comm;
        private NodeMaster.NodeMaster? master;
        private NodeAgent.NodeAgent? agent;
        private NodeScheduler scheduler;
        private SchedulerMode selectedMode = SchedulerMode.Auto;
        private List<AgentStatus> agentStatuses = new();
        private List<string> taskHistory = new();
        private ObservableCollection<TaskInfo> tasks = new();
        private System.Timers.Timer heartbeatTimer;
        private DateTime masterStartTime;
        private int agentCount = 0;
        private bool IsProVersion = false;
        private int taskSequenceId = 0;
        private Dictionary<string, List<(int SequenceId, string Result)>> computeResults = new();
        private const string configPath = "node.config";
        private const string settingsPath = "settings.json";
        private NodeLinkSettings settings = new();
        private IZeroconfHost masterHost;
        private IZeroconfHost agentHost;
        private System.Timers.Timer agentDiscoveryTimer;

        public MainWindow()
        {
            InitializeComponent();
            currentRole = LoadRoleFromConfig();
            comm = new CommChannel();
            tasks = new ObservableCollection<TaskInfo>();
            TaskListBox.ItemsSource = tasks;
            LoadSettings();
            InitializeRole(currentRole);
            UpdateRoleUI();
            SetupHeartbeat();
            comm.OnMessageReceived += HandleAgentMessage;
            if (currentRole == NodeRole.Master)
            {
                SetupAgentDiscovery();
            }
        }

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

        private async void InitializeRole(NodeRole role)
        {
            RoleLabel.Text = $"Current Role: {role}";
            string localIp = GetLocalIPAddress();

            if (role == NodeRole.Master)
            {
                // Advertise Master mDNS service
                var service = new ServiceRegistration
                {
                    Name = "NodeLinkMaster",
                    RegType = "_nodelink._tcp.",
                    Port = 5000,
                    HostName = Environment.MachineName,
                    // Zeroconf.NET expects IPs as a list of strings
                    IPAddress = new List<string> { localIp }
                };
                masterHost = await ZeroconfResolver.RegisterServiceAsync(service);
                Log("mDNS service advertised: _nodelink._tcp.local.");
                master = new NodeMaster.NodeMaster(comm);
                masterStartTime = DateTime.Now;
                scheduler = new NodeScheduler(agentStatuses);
                scheduler.SetMode(selectedMode);
                MasterStatusLabel.Text = "Online";
                MasterStatusLabel.Foreground = Brushes.Green;
                // Add Master to NodeGrid
                agentStatuses.Add(new AgentStatus
                {
                    AgentId = "Master",
                    IsOnline = true,
                    IpAddress = localIp,
                    CpuUsagePercent = 0f,
                    GpuUsagePercent = 0f,
                    MemoryAvailableMB = 0f,
                    NetworkMbps = 0f,
                    HasFileAccess = true,
                    AvailableFiles = Array.Empty<string>(),
                    TaskQueueLength = 0,
                    LastHeartbeat = DateTime.Now
                });
                NodeGrid.ItemsSource = null;
                NodeGrid.ItemsSource = agentStatuses;
            }
            else
            {
                // Advertise Agent mDNS service
                var service = new ServiceRegistration
                {
                    Name = $"NodeLinkAgent-{Environment.MachineName}",
                    RegType = "_nodelink-agent._tcp.",
                    Port = 5000,
                    HostName = Environment.MachineName,
                    IPAddress = new List<string> { localIp }
                };
                agentHost = await ZeroconfResolver.RegisterServiceAsync(service);
                Log("mDNS service advertised: _nodelink-agent._tcp.local.");
                // Discover Master via mDNS
                string masterIp = await DiscoverMasterIpAsync();
                if (string.IsNullOrEmpty(masterIp))
                {
                    masterIp = settings.MasterIp;
                    Log($"mDNS discovery failed, using fallback IP: {masterIp}");
                }
                else
                {
                    settings.MasterIp = masterIp;
                    SaveSettings();
                    Log($"Discovered Master IP: {masterIp}");
                }
                scheduler = null;
                var agentId = $"Agent-{Environment.MachineName}";
                agent = new NodeAgent.NodeAgent(agentId, comm);
                comm.SetMasterIp(masterIp); // Assuming CommChannel has a method to set Master IP
                // Add Agent to NodeGrid
                agentStatuses.Add(new AgentStatus
                {
                    AgentId = agentId,
                    IsOnline = true,
                    IpAddress = localIp,
                    CpuUsagePercent = 0f,
                    GpuUsagePercent = 0f,
                    MemoryAvailableMB = 0f,
                    NetworkMbps = 0f,
                    HasFileAccess = true,
                    AvailableFiles = Array.Empty<string>(),
                    TaskQueueLength = 0,
                    LastHeartbeat = DateTime.Now
                });
                NodeGrid.ItemsSource = null;
                NodeGrid.ItemsSource = agentStatuses;
            }
        }

        private async Task<string> DiscoverMasterIpAsync()
        {
            try
            {
                var responses = await ZeroconfResolver.ResolveAsync("_nodelink._tcp.local.", TimeSpan.FromSeconds(5));
                var host = responses.FirstOrDefault();
                if (host != null)
                {
                    return host.IPAddress;
                }
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
                var responses = await ZeroconfResolver.ResolveAsync("_nodelink-agent._tcp.local.", TimeSpan.FromSeconds(5));
                foreach (var host in responses)
                {
                    // Use DisplayName or another property instead of ServiceName
                    var agentId = host.DisplayName.Replace("NodeLinkAgent-", "");
                    var existing = agentStatuses.FirstOrDefault(a => a.AgentId == agentId);
                    if (existing == null && agentId != "Master")
                    {
                        master?.RegisterAgent(agentId, message => HandleAgentMessage(agentId, message));
                        agentStatuses.Add(new AgentStatus
                        {
                            AgentId = agentId,
                            IsOnline = true,
                            IpAddress = host.IPAddress,
                            CpuUsagePercent = 0f,
                            GpuUsagePercent = 0f,
                            MemoryAvailableMB = 0f,
                            NetworkMbps = 0f,
                            HasFileAccess = true,
                            AvailableFiles = Array.Empty<string>(),
                            TaskQueueLength = 0,
                            LastHeartbeat = DateTime.Now
                        });
                        Log($"Discovered Agent {agentId} at {host.IPAddress}");
                    }
                    else if (existing != null)
                    {
                        existing.IpAddress = host.IPAddress;
                        existing.IsOnline = true;
                    }
                    Dispatcher.Invoke(() =>
                    {
                        NodeGrid.ItemsSource = null;
                        NodeGrid.ItemsSource = agentStatuses;
                    });
                }
            }
            catch (Exception ex)
            {
                Log($"mDNS Agent discovery error: {ex.Message}");
            }
        }

        private void SetupAgentDiscovery()
        {
            agentDiscoveryTimer = new System.Timers.Timer(10000); // Check every 10s
            agentDiscoveryTimer.Elapsed += (s, e) => DiscoverAgentsAsync().GetAwaiter().GetResult();
            agentDiscoveryTimer.AutoReset = true;
            agentDiscoveryTimer.Start();
        }

        private string GetLocalIPAddress()
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus == OperationalStatus.Up)
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
            return "Unknown";
        }

        private void UpdateRoleUI()
        {
            bool masterOnline = currentRole == NodeRole.Master || IsMasterOnline(settings.MasterIp);
            Dispatcher.Invoke(() =>
            {
                SwitchRoleButton.IsEnabled = !masterOnline || currentRole == NodeRole.Master;
                RegisterAgentButton.IsEnabled = currentRole == NodeRole.Master;
                DispatchTaskButton.IsEnabled = currentRole == NodeRole.Master;
                BroadcastButton.IsEnabled = currentRole == NodeRole.Master;
                TaskInputBox.IsEnabled = currentRole == NodeRole.Master;
                CustomTaskButton.IsEnabled = currentRole == NodeRole.Master;
                StopAllTasksButton.IsEnabled = currentRole == NodeRole.Master;
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
                if (masterHost != null)
                {
                    masterHost.Dispose();
                }
                if (agentHost != null)
                {
                    agentHost.Dispose();
                }
                if (agentDiscoveryTimer != null)
                {
                    agentDiscoveryTimer.Stop();
                    agentDiscoveryTimer.Dispose();
                }
                string logEntry = $"Restart triggered at {DateTime.Now:yyyy-MM-dd HH:mm:ss} for role: {newRole}\n";
                File.AppendAllText("restart.log", logEntry);
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
                if (masterHost != null)
                {
                    masterHost.Dispose();
                }
                if (agentHost != null)
                {
                    agentHost.Dispose();
                }
                if (agentDiscoveryTimer != null)
                {
                    agentDiscoveryTimer.Stop();
                    agentDiscoveryTimer.Dispose();
                }
                SaveSettings();
                string logEntry = $"App exited at {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n";
                File.AppendAllText("restart.log", logEntry);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                Log($"Failed to exit cleanly: {ex.Message}");
            }
        }

        private void ApplySettings()
        {
            Log($"Heartbeat Interval: {settings.HeartbeatIntervalMs} ms");
            Log($"Use GPU Globally: {settings.UseGpuGlobally}");
            heartbeatTimer.Interval = settings.HeartbeatIntervalMs;
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow(settings, agentStatuses, IsProVersion);
            settingsWindow.Owner = this;
            settingsWindow.ShowDialog();
            ApplySettings();
        }

        private void HandleAgentMessage(string agentId, string message)
        {
            Log($"Message from {agentId}: {message}");
            if (message.StartsWith("AgentStatus:"))
            {
                var json = message.Substring("AgentStatus:".Length);
                try
                {
                    var status = AgentStatus.FromJson(json);
                    var existing = agentStatuses.FirstOrDefault(a => a.AgentId == status.AgentId);
                    if (existing != null)
                    {
                        agentStatuses.Remove(existing);
                    }
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
                    Log($"Updated status for {status.AgentId}");
                }
                catch (Exception ex)
                {
                    Log($"Failed to parse AgentStatus: {ex.Message}");
                }
            }
            else if (message.StartsWith("ComputeResult:"))
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
            }
            else if (message.StartsWith("CustomTask:"))
            {
                var parts = message.Split(':');
                if (parts.Length > 2)
                {
                    var taskId = parts[1];
                    var description = string.Join(":", parts, 2, parts.Length - 2);
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
            }
            else if (message.StartsWith("StopTask:"))
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
            }
            else if (message == "StopAllTasks")
            {
                Dispatcher.Invoke(() =>
                {
                    tasks.Clear();
                    taskHistory.Add($"{DateTime.Now:T}: Stopped all tasks");
                    TaskCountLabel.Text = "0";
                });
                Log("Stopped all tasks");
            }
            else if (message.StartsWith("Result:") || message.StartsWith("Launched:") || message.StartsWith("Launch failed:"))
            {
                Log($"Task response: {message}");
                taskHistory.Add($"{DateTime.Now:T}: {message}");
            }
        }

        private void Broadcast_Click(object sender, RoutedEventArgs e)
        {
            string message = $"Ping-{DateTime.Now.Ticks}";
            comm.Broadcast(message);
            Log($"Broadcasted: {message}");
        }

        private void SetupHeartbeat()
        {
            heartbeatTimer = new System.Timers.Timer(settings.HeartbeatIntervalMs);
            heartbeatTimer.Elapsed += (s, e) => Dispatcher.Invoke(UpdateHeartbeat);
            heartbeatTimer.AutoReset = true;
            heartbeatTimer.Start();
        }

        private void UpdateHeartbeat()
        {
            if (currentRole == NodeRole.Master)
            {
                MasterUptimeLabel.Text = (DateTime.Now - masterStartTime).ToString(@"hh\:mm\:ss");
                if (master != null)
                {
                    agentStatuses = master.GetAgentStatuses();
                }
                Dispatcher.Invoke(() =>
                {
                    NodeGrid.ItemsSource = null;
                    NodeGrid.ItemsSource = agentStatuses;
                    AlertBox.Items.Clear();
                    foreach (var status in agentStatuses)
                    {
                        if (status.CpuUsagePercent > 85)
                            AlertBox.Items.Add($"{status.AgentId} high CPU: {status.CpuUsagePercent}%");
                        if (!status.IsOnline)
                            AlertBox.Items.Add($"{status.AgentId} is offline");
                        if (status.HasGpu)
                            AlertBox.Items.Add($"{status.AgentId} GPU: {status.GpuModel} ({status.GpuMemoryMB}MB)");
                        else
                            AlertBox.Items.Add($"{status.AgentId} has no GPU");
                    }
                });
            }
            else
            {
                string localIp = GetLocalIPAddress();
                var status = new AgentStatus
                {
                    AgentId = $"Agent-{Environment.MachineName}",
                    IsOnline = true,
                    IpAddress = localIp,
                    CpuUsagePercent = 10f,
                    GpuUsagePercent = 0f,
                    MemoryAvailableMB = 4000f,
                    NetworkMbps = 0f,
                    HasFileAccess = true,
                    AvailableFiles = Array.Empty<string>(),
                    TaskQueueLength = tasks.Count(t => t.AgentId == $"Agent-{Environment.MachineName}"),
                    LastHeartbeat = DateTime.Now,
                    HasGpu = false,
                    HasCuda = false,
                    GpuModel = "",
                    GpuMemoryMB = 0,
                    LastTaskDuration = TimeSpan.Zero,
                    DiskReadMBps = 0f,
                    DiskWriteMBps = 0f
                };
                comm.SendToMaster($"AgentStatus:{status.ToJson()}");
                Log($"Heartbeat sent from agent at {DateTime.Now:T}");
            }
        }

        private void SchedulerModeDropdown_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (SchedulerModeDropdown.SelectedItem is ComboBoxItem selectedItem)
            {
                string modeText = selectedItem.Content.ToString();
                if (Enum.TryParse(modeText, out SchedulerMode mode))
                {
                    selectedMode = mode;
                    if (currentRole == NodeRole.Master && scheduler != null)
                    {
                        scheduler.SetMode(mode);
                        Log($"Scheduler mode set to: {mode}");
                    }
                    else
                    {
                        Log("Scheduler mode change ignored — not in Master role.");
                    }
                }
                else
                {
                    Log($"Invalid scheduler mode: {modeText}");
                }
            }
        }

        private void CustomTaskButton_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole == NodeRole.Master)
            {
                string task = TaskInputBox.Text;
                if (!string.IsNullOrEmpty(task))
                {
                    var agent = agentStatuses.FirstOrDefault(a => a.IsOnline && a.AgentId != "Master");
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
                        Log("No available agents to assign task");
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
            if (currentRole == NodeRole.Master)
            {
                master?.DispatchTaskToAgent("StopAllTasks", null); // Broadcast to all agents
                Log("Requested to stop all tasks");
                taskHistory.Add($"{DateTime.Now:T}: Requested to stop all tasks");
            }
            else
            {
                Log("Tasks can only be stopped in Master mode");
            }
        }

        private void RegisterAgent_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole != NodeRole.Master || master is null) return;

            var agentId = $"Agent-{++agentCount}";
            master.RegisterAgent(agentId, message => HandleAgentMessage(agentId, message));

            var status = new AgentStatus
            {
                AgentId = agentId,
                IsOnline = true,
                IpAddress = GetLocalIPAddress(),
                CpuUsagePercent = 0f,
                GpuUsagePercent = 0f,
                MemoryAvailableMB = 0f,
                NetworkMbps = 0f,
                HasFileAccess = true,
                AvailableFiles = Array.Empty<string>(),
                TaskQueueLength = 0,
                LastHeartbeat = DateTime.Now,
                HasGpu = false,
                HasCuda = false,
                GpuModel = "",
                GpuMemoryMB = 0,
                LastTaskDuration = TimeSpan.Zero,
                DiskReadMBps = 0f,
                DiskWriteMBps = 0f
            };

            agentStatuses.Add(status);
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

        private void DispatchComputeTasksForApp(NominatedApp app)
        {
            bool preferGpu = settings.UseGpuGlobally;

            var eligibleAgents = agentStatuses
                .Where(a => a.IsOnline && a.AgentId != "Master" && (!preferGpu || a.HasGpu))
                .ToList();

            if (!eligibleAgents.Any())
            {
                Log($"No suitable agents available for {app.Name} (GPU preferred: {preferGpu})");
                return;
            }

            for (int i = 0; i < 10; i++)
            {
                scheduler = new NodeScheduler(eligibleAgents);
                scheduler.SetMode(selectedMode);
                string taskPayload = $"Compute:{app.Name}|{taskSequenceId}|task_{i}";
                string selectedAgentId = scheduler.SelectAgent(taskPayload, null);

                if (selectedAgentId == "None")
                {
                    Log($"No suitable agents for compute task {app.Name} (task {i})");
                    continue;
                }

                master?.DispatchTaskToAgent(taskPayload, selectedAgentId);
                taskHistory.Add(taskPayload);
                Dispatcher.Invoke(() =>
                {
                    tasks.Add(new TaskInfo
                    {
                        TaskId = $"task_{taskSequenceId}",
                        Description = taskPayload,
                        AgentId = selectedAgentId,
                        Timestamp = DateTime.Now
                    });
                    TaskCountLabel.Text = tasks.Count.ToString();
                });
                Log($"Dispatched compute task {app.Name} (task {i}) to {selectedAgentId}");
                taskSequenceId++;
            }
        }

        private void ReassembleComputeResults(string appName)
        {
            if (computeResults.TryGetValue(appName, out var results))
            {
                var orderedResults = results.OrderBy(r => r.SequenceId).Select(r => r.Result).ToList();
                Log($"Reassembled results for {appName}: {string.Join(", ", orderedResults)}");
            }
        }
    }
}

