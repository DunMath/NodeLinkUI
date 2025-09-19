using NodeAgent;
using NodeComm;
using NodeCore;
using NodeCore.NodeCore;
using NodeCore.Scheduling;
using NodeMaster;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

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
        private System.Timers.Timer heartbeatTimer;
        private DateTime masterStartTime;
        private int agentCount = 0;

        private const string configPath = "node.config";
        private const string settingsPath = "settings.json";
        private const string masterIp = "192.168.1.100"; // Replace with actual Master IP

        private NodeLinkSettings settings = new();

        public MainWindow()
        {
            InitializeComponent();

            currentRole = LoadRoleFromConfig();
            comm = new CommChannel();

            // 1. Load settings from disk
            LoadSettings();

            

            // 3. Continue with your existing initialization
            InitializeRole(currentRole);
            UpdateRoleUI();
            SetupHeartbeat();
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(settingsPath))
                {
                    var json = File.ReadAllText(settingsPath);
                    settings = JsonSerializer.Deserialize<NodeLinkSettings>(json) ?? new NodeLinkSettings();
                    LogBox.Items.Add("Settings loaded.");
                }
                else
                {
                    settings = new NodeLinkSettings();
                    SaveSettings(); // create default file
                    LogBox.Items.Add("Default settings created.");
                }
            }
            catch (Exception ex)
            {
                settings = new NodeLinkSettings();
                LogBox.Items.Add($"Failed to load settings: {ex.Message}");
            }
        }

        private void SaveSettings()
        {
            try
            {
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(settingsPath, json);
                LogBox.Items.Add("Settings saved.");
            }
            catch (Exception ex)
            {
                LogBox.Items.Add($"Failed to save settings: {ex.Message}");
            }
        }

        private void InitializeRole(NodeRole role)
        {
            RoleLabel.Text = $"Current Role: {role}";

            if (role == NodeRole.Master)
            {
                master = new NodeMaster.NodeMaster(comm);
                masterStartTime = DateTime.Now;
                scheduler = new NodeScheduler(agentStatuses);
                scheduler.SetMode(selectedMode);

                MasterStatusLabel.Text = "Online";
                MasterStatusLabel.Foreground = System.Windows.Media.Brushes.Green;
            }
            else
            {
                scheduler = null; // Ensure scheduler is disabled for Agent role

                var agentId = $"Agent-{Environment.MachineName}";
                agent = new NodeAgent.NodeAgent(agentId, comm);

                MasterStatusLabel.Text = "Offline";
                MasterStatusLabel.Foreground = System.Windows.Media.Brushes.Red;
            }
        }

        private void UpdateRoleUI()
        {
            bool masterOnline = IsMasterOnline(masterIp);
            SwitchRoleButton.IsEnabled = !masterOnline || currentRole == NodeRole.Master;

            if (masterOnline)
                LogBox.Items.Add("Master is online — role switch disabled.");
            else
                LogBox.Items.Add("Master not detected — promotion available.");
        }

        private bool IsMasterOnline(string ip)
        {
            try
            {
                using var ping = new Ping();
                var reply = ping.Send(ip, 1000);
                return reply.Status == IPStatus.Success;
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
                LogBox.Items.Add($"Switched to {newRole}");
                RestartApp(newRole);
            }
            else
            {
                LogBox.Items.Add("Role switch cancelled.");
            }
        }

        private void RestartApp(NodeRole newRole)
        {
            try
            {
                string logEntry = $"Restart triggered at {DateTime.Now:yyyy-MM-dd HH:mm:ss} for role: {newRole}\n";
                File.AppendAllText("restart.log", logEntry);

                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    System.Diagnostics.Process.Start(exePath);
                }
                else if (LogBox?.Items != null)
                {
                    LogBox.Items.Add("Failed to locate executable path for restart.");
                }
            }
            catch (Exception ex)
            {
                if (LogBox?.Items != null)
                    LogBox.Items.Add($"Failed to restart app: {ex.Message}");
            }

            Application.Current.Shutdown();
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveSettings();
                string logEntry = $"App exited at {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n";
                File.AppendAllText("restart.log", logEntry);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                if (LogBox?.Items != null)
                    LogBox.Items.Add($"Failed to exit cleanly: {ex.Message}");
            }
        }
        private void ApplySettings()
        {
            // For now, just a placeholder so the call compiles.
            // Later you can wire this into your runtime (heartbeat timers, GPU preference, etc.)

            // Example: show current values in the console or logs
            Console.WriteLine($"Master IP: {settings.MasterIp}");
            Console.WriteLine($"Heartbeat Interval: {settings.HeartbeatIntervalMs} ms");
            Console.WriteLine($"Use GPU Globally: {settings.UseGpuGlobally}");
        }
        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow(settings);
            settingsWindow.Owner = this;
            settingsWindow.ShowDialog();

            // After closing, reapply settings
            ApplySettings();
        }

        private void HandleAgentMessage(string agentId, string message)
        {
            LogBox.Items.Add($"Message from {agentId}: {message}");
            // You can add task parsing, UI updates, or telemetry here
        }

        private void Broadcast_Click(object sender, RoutedEventArgs e)
        {
            string message = $"Broadcast-{DateTime.Now.Ticks}";
            comm.Broadcast(message);
            LogBox.Items.Add($"Broadcasted: {message}");
        }

        private void SetupHeartbeat()
        {
            heartbeatTimer = new System.Timers.Timer(5000); // every 5 seconds
            heartbeatTimer.Elapsed += (s, e) => Dispatcher.Invoke(UpdateHeartbeat);
            heartbeatTimer.Start();
        }

        private void UpdateHeartbeat()
        {
            if (currentRole == NodeRole.Master)
            {
                MasterUptimeLabel.Text = (DateTime.Now - masterStartTime).ToString(@"hh\:mm\:ss");

                if (master != null)
                {
                    var agents = master.GetAgentStatuses().ToList();
                    agentStatuses = master.GetAgentStatuses();
                }

                AgentGrid.ItemsSource = null;
                AgentGrid.ItemsSource = agentStatuses;

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
            }
            else
            {
                LogBox.Items.Add($"Heartbeat sent from agent at {DateTime.Now:T}");
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
                        if (LogBox?.Items != null)
                            LogBox.Items.Add($"Scheduler mode set to: {mode}");
                    }
                    else
                    {
                        if (LogBox?.Items != null)
                            LogBox.Items.Add("Scheduler mode change ignored — not in Master role.");
                    }
                }
                else
                {
                    if (LogBox?.Items != null)
                        LogBox.Items.Add($"Invalid scheduler mode: {modeText}");
                }
            }
        }
    }
}

