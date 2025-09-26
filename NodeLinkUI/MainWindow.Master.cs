using NodeComm;
using NodeCore;
using NodeCore.Scheduling;
using NodeMaster;
using NodeAgent;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace NodeLinkUI
{
    public partial class MainWindow : Window
    {
             

        private void RefreshAgentStatuses()
        {
            if (master is null) return;

            agentStatuses = master.GetAgentStatuses();
            NodeGrid.ItemsSource = agentStatuses;

            foreach (var status in agentStatuses)
            {
                if (status.HasGpu)
                {
                    LogBox.Items.Add($"{status.AgentId} GPU: {status.GpuModel} ({status.GpuMemoryMB}MB)");
                }
                else
                {
                    LogBox.Items.Add($"{status.AgentId} has no GPU");
                }
            }
        }

        private void MonitorAgentStatus()
        {
            if (currentRole == NodeRole.Master)
            {
                foreach (var status in agentStatuses)
                {
                    if ((DateTime.Now - status.LastHeartbeat).TotalSeconds > 10)
                    {
                        status.IsOnline = false;
                        LogBox.Items.Add($"Agent {status.AgentId} is offline");
                        AlertBox.Items.Add($"Agent {status.AgentId} offline");
                    }
                    if (status.CpuUsagePercent > 90)
                    {
                        AlertBox.Items.Add($"High CPU usage on {status.AgentId}: {status.CpuUsagePercent}%");
                    }
                    if (status.GpuUsagePercent > 90 && status.HasGpu)
                    {
                        AlertBox.Items.Add($"High GPU usage on {status.AgentId}: {status.GpuUsagePercent}%");
                    }
                }
                NodeGrid.Items.Refresh();
            }
        }

        private void SaveState_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole == NodeRole.Master)
            {
                File.WriteAllText("state.json", JsonSerializer.Serialize(new
                {
                    AgentStatuses = agentStatuses,
                    TaskHistory = taskHistory,
                    CurrentRole = currentRole
                }));
                LogBox.Items.Add("State saved to state.json");
            }
            else
            {
                LogBox.Items.Add("State saving only available in Master mode");
            }
        }

        private void LoadState_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole == NodeRole.Master && File.Exists("state.json"))
            {
                var state = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText("state.json"));
                if (state.ContainsKey("AgentStatuses"))
                {
                    agentStatuses = JsonSerializer.Deserialize<List<AgentStatus>>(state["AgentStatuses"].ToString());
                    NodeGrid.ItemsSource = agentStatuses;
                    NodeGrid.Items.Refresh();
                }
                if (state.ContainsKey("TaskHistory"))
                {
                    taskHistory = JsonSerializer.Deserialize<List<string>>(state["TaskHistory"].ToString());
                    TaskListBox.ItemsSource = taskHistory;
                    TaskListBox.Items.Refresh();
                    TaskCountLabel.Text = taskHistory.Count.ToString();
                }
                LogBox.Items.Add("State loaded from state.json");
            }
            else
            {
                LogBox.Items.Add("No state file found or not in Master mode");
            }
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogBox.Items.Clear();
            LogBox.Items.Add("Log cleared");
        }

        private void ClearAlerts_Click(object sender, RoutedEventArgs e)
        {
            AlertBox.Items.Clear();
            LogBox.Items.Add("Alerts cleared");
        }

        private void RefreshAgents_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole == NodeRole.Master)
            {
                RefreshAgentStatuses();
                MonitorAgentStatus();
                LogBox.Items.Add("Agent statuses refreshed");
            }
            else
            {
                LogBox.Items.Add("Agent refresh only available in Master mode");
            }
        }

        private void RemoveAgent_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole == NodeRole.Master && NodeGrid.SelectedItem is AgentStatus selectedAgent)
            {
                agentStatuses.Remove(selectedAgent);
                NodeGrid.Items.Refresh();
                LogBox.Items.Add($"Removed agent {selectedAgent.AgentId}");
            }
            else
            {
                LogBox.Items.Add("Select an agent to remove in Master mode");
            }
        }

        private void StopTask_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole == NodeRole.Master && TaskListBox.SelectedItem is string taskPayload)
            {
                var parts = taskPayload.Split(':');
                if (parts.Length >= 3)
                {
                    string taskId = parts[1];
                    string agentId = parts.Last();
                    comm.SendToAgent(agentId, $"StopTask:{taskId}");
                    taskHistory.Remove(taskPayload);
                    TaskListBox.Items.Refresh();
                    TaskCountLabel.Text = taskHistory.Count.ToString();
                    LogBox.Items.Add($"Requested to stop task {taskId} on {agentId}");
                }
            }
            else
            {
                LogBox.Items.Add("Select a task to stop in Master mode");
            }
        }

        private void UpdateSchedulerSettings_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole == NodeRole.Master)
            {
                scheduler.SetMode(SchedulerMode.Auto); // Example: Update as needed
                LogBox.Items.Add("Scheduler settings updated to Balanced mode");
            }
            else
            {
                LogBox.Items.Add("Scheduler settings only available in Master mode");
            }
        }
    }
}
