using System;
using System.Windows;
using System.Windows.Controls;
using System.Collections.Generic;
using System.Management;
using NodeCore;
using NodeComm;
using NodeAgent;

namespace NodeLinkUI
{
    public partial class MainWindow : Window
    {
        private void SendHeartbeat_Click(object sender, RoutedEventArgs e)
        {
            if (currentRole != NodeRole.Agent || agent == null) return;

            var status = new AgentStatus
            {
                AgentId = agent.Id,
                IsOnline = true,
                LastHeartbeat = DateTime.Now,
                CpuUsagePercent = GetCpuUsage(),
                GpuUsagePercent = GetGpuUsage(),
                MemoryAvailableMB = GetAvailableMemory(),
                NetworkMbps = GetNetworkSpeed(),
                HasFileAccess = true,
                AvailableFiles = GetAvailableFiles(),

                // 🧩 GPU capability reporting
                HasGpu = TryDetectGpu(out string model, out int memoryMB),
                HasCuda = false, // Reserved for future CUDA support
                GpuModel = model,
                GpuMemoryMB = memoryMB
            };

            comm.SendToMaster(status.ToJson());
            LogBox.Items.Add($"Heartbeat sent from {status.AgentId}");
        }

        // Stub methods — replace with actual telemetry logic
        private int GetCpuUsage() => 42;
        private int GetGpuUsage() => 17;
        private int GetAvailableMemory() => 2048;
        private int GetNetworkSpeed() => 120;
        private string[] GetAvailableFiles() => new[] { "log.txt", "data.json" };

        // 🧠 GPU detection helpers
        private bool TryDetectGpu(out string model, out int memoryMB)
        {
            model = "No GPU detected";
            memoryMB = 0;

            try
            {
                var searcher = new ManagementObjectSearcher("select * from Win32_VideoController");
                foreach (var obj in searcher.Get())
                {
                    model = obj["Name"]?.ToString() ?? "Unknown GPU";

                    if (obj["AdapterRAM"] != null)
                    {
                        var memBytes = Convert.ToInt64(obj["AdapterRAM"]);
                        memoryMB = (int)(memBytes / (1024 * 1024));
                    }

                    return true; // GPU found
                }
            }
            catch
            {
                model = "GPU detection failed";
                memoryMB = 0;
            }

            return false; // No GPU found
        }
    }
}



