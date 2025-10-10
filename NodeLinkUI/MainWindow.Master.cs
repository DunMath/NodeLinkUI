// File: NodeLinkUI/MainWindow.Master.cs  (AMENDED for ObservableCollection + in-place updates)
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
        // DTO used for state save/load
        private sealed class UiState
        {
            public List<AgentStatus>? AgentStatuses { get; set; }
            public List<string>? TaskHistory { get; set; }
            public NodeRole CurrentRole { get; set; }
        }

        /// <summary>
        /// Pulls statuses from NodeMaster and merges them into the bound ObservableCollection.
        /// </summary>
        private void RefreshAgentStatuses()
        {
            if (master is null) return;

            var list = master.GetAgentStatuses(); // List<AgentStatus>

            // Merge in place — no rebinds; preserves selection & row colors.
            foreach (var s in list)
            {
                // Also mark presence "seen" now so presence-smoothing is happier.
                _lastSeenUtc[s.AgentId] = DateTime.UtcNow;
                UpdateOrAddAgentStatus(s);
            }

            // Optionally log GPU info
            foreach (var status in agentStatuses)
            {
                if (status.AgentId.Equals("Master", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (status.HasGpu)
                    Log($"{status.AgentId} GPU: {status.GpuModel} ({status.GpuMemoryMB}MB)");
                else
                    Log($"{status.AgentId} has no GPU");
            }

            RefreshGridPreservingSelection();
        }

        /// <summary>
        /// Adds basic alerts and offline detection. Uses in-place updates and one refresh.
        /// </summary>
        private void MonitorAgentStatus()
        {
            if (currentRole != NodeRole.Master) return;

            foreach (var status in agentStatuses)
            {
                if ((DateTime.Now - status.LastHeartbeat).TotalSeconds > 10)
                {
                    status.IsOnline = false;
                    Log($"Agent {status.AgentId} is offline");
                    AlertBox.Items.Add($"Agent {status.AgentId} offline");
                }

                if (status.CpuUsagePercent > 90)
                    AlertBox.Items.Add($"High CPU usage on {status.AgentId}: {status.CpuUsagePercent:F0}%");

                if (status.HasGpu && status.GpuUsagePercent > 90)
                    AlertBox.Items.Add($"High GPU usage on {status.AgentId}: {status.GpuUsagePercent:F0}%");
            }

            RefreshGridPreservingSelection();
        }

        private void SaveState_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole != NodeRole.Master)
            {
                Log("State saving only available in Master mode");
                return;
            }

            try
            {
                var state = new UiState
                {
                    AgentStatuses = agentStatuses.ToList(),
                    TaskHistory = taskHistory,
                    CurrentRole = currentRole
                };
                File.WriteAllText("state.json", JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
                Log("State saved to state.json");
            }
            catch (Exception ex)
            {
                Log($"Failed to save state: {ex.Message}");
            }
        }

        private void LoadState_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole != NodeRole.Master)
            {
                Log("State loading only available in Master mode");
                return;
            }

            if (!File.Exists("state.json"))
            {
                Log("No state.json file found");
                return;
            }

            try
            {
                var state = JsonSerializer.Deserialize<UiState>(File.ReadAllText("state.json"));
                if (state != null)
                {
                    if (state.AgentStatuses != null)
                    {
                        // Replace contents, not the collection
                        agentStatuses.Clear();
                        foreach (var s in state.AgentStatuses)
                        {
                            _lastSeenUtc[s.AgentId] = DateTime.UtcNow;
                            agentStatuses.Add(s);
                        }
                        RefreshGridPreservingSelection();
                    }

                    if (state.TaskHistory != null)
                    {
                        taskHistory = state.TaskHistory;
                        // We keep TaskListBox bound to 'tasks' (TaskInfo) in the main app.
                        // Here we just reflect a count if you show one.
                        try { TaskCountLabel.Text = tasks.Count.ToString(); } catch { }
                    }

                    Log("State loaded from state.json");
                }
                else
                {
                    Log("State file was empty or invalid");
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to load state: {ex.Message}");
            }
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogBox.Items.Clear();
            Log("Log cleared");
        }

        private void ClearAlerts_Click(object sender, RoutedEventArgs e)
        {
            AlertBox.Items.Clear();
            Log("Alerts cleared");
        }

        private void RefreshAgents_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole != NodeRole.Master)
            {
                Log("Agent refresh only available in Master mode");
                return;
            }

            RefreshAgentStatuses();
            MonitorAgentStatus();
            Log("Agent statuses refreshed");
        }

        private void RemoveAgent_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole != NodeRole.Master)
            {
                Log("Select an agent to remove in Master mode");
                return;
            }

            if (NodeGrid.SelectedItem is AgentStatus selectedAgent)
            {
                agentStatuses.Remove(selectedAgent);
                RefreshGridPreservingSelection();
                Log($"Removed agent {selectedAgent.AgentId}");
            }
            else
            {
                Log("Select an agent row first");
            }
        }

        private void StopTask_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole != NodeRole.Master)
            {
                Log("Stop Task is only available in Master mode");
                return;
            }

            // Your XAML binds TaskListBox to TaskInfo items (not strings).
            if (TaskListBox.SelectedItem is TaskInfo selected)
            {
                var taskId = selected.TaskId;
                var agentId = selected.AgentId;

                // Prefer NodeMaster path; fallback to CommChannel direct
                if (master != null)
                    master.DispatchTaskToAgent($"StopTask:{taskId}", agentId);
                else
                    comm.SendToAgent(agentId, $"StopTask:{taskId}");

                tasks.Remove(selected);
                try { TaskCountLabel.Text = tasks.Count.ToString(); } catch { }

                Log($"Requested to stop task {taskId} on {agentId}");
            }
            else
            {
                Log("Select a task to stop");
            }
        }

        private void UpdateSchedulerSettings_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole != NodeRole.Master)
            {
                Log("Scheduler settings only available in Master mode");
                return;
            }

            scheduler?.SetMode(SchedulerMode.Auto); // as needed
            Log("Scheduler settings updated to Auto mode");
        }
    }
}

