using NodeComm;
using NodeCore;
using NodeMaster;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace NodeLinkUI
{
    public partial class MainWindow : Window
    {
       
       
        private void RegisterAgent_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole != NodeRole.Master || master == null) return;

            var agentId = $"Agent-{++agentCount}";
            var newAgent = new NodeAgent.NodeAgent(agentId, comm);

            master.RegisterAgent(agentId, message => HandleAgentMessage(agentId, message));

            var status = new AgentStatus
            {
                AgentId = agentId,
                IsOnline = true,
                LastHeartbeat = DateTime.Now,
                CpuUsagePercent = 0,
                GpuUsagePercent = 0,
                MemoryAvailableMB = 0,
                NetworkMbps = 0,
                HasFileAccess = true,
                AvailableFiles = Array.Empty<string>()
            };

            agentStatuses.Add(status);
            LogBox.Items.Add($"Registered {agentId}");
        }

        private void DispatchTask_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole != NodeRole.Master || master == null) return;

            if (settings.NominatedApps.Count == 0)
            {
                LogBox.Items.Add("No nominated apps available for dispatch.");
                return;
            }

            foreach (var app in settings.NominatedApps)
            {
                DispatchTaskForApp(app);
            }
        }

        private void DispatchTaskForApp(NominatedApp app)
        {
            bool preferGpu = settings.UseGpuGlobally;

            var eligibleAgents = agentStatuses
                .Where(a => a.IsOnline && (!preferGpu || a.HasGpu))
                .OrderBy(a => a.IsBusy)
                .ToList();

            if (!eligibleAgents.Any())
            {
                LogBox.Items.Add($"No suitable agents available for {app.Name} (GPU preferred: {preferGpu})");
                return;
            }

            var selectedAgent = eligibleAgents.First();
            string taskPayload = $"Launch:{app.Name}|{app.Path}";

            master.DispatchTaskToAgent(selectedAgent.AgentId, taskPayload);
            taskHistory.Add(taskPayload);
            TaskListBox.Items.Add(taskPayload);
            TaskCountLabel.Text = taskHistory.Count.ToString();

            LogBox.Items.Add($"Dispatched {app.Name} to {selectedAgent.AgentId} (GPU: {selectedAgent.HasGpu})");
        }

        private void RefreshAgentStatuses()
        {
            agentStatuses = master.GetAgentStatuses();
            AgentGrid.ItemsSource = agentStatuses;

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
    }
}





