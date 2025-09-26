using System;
using System.Diagnostics;
using System.Threading;
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
                HasGpu = TryDetectGpu(out string model, out int memoryMB),
                HasCuda = false, // Reserved for future CUDA support
                GpuModel = model,
                GpuMemoryMB = memoryMB
            };

            Task.Run(() => comm.SendToMaster(status.ToJson()));
            this.Log($"Heartbeat sent from {status.AgentId}");
        }

        private float GetCpuUsage()
        {
            try
            {
                using var counter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                counter.NextValue();
                Thread.Sleep(1000);
                return counter.NextValue();
            }
            catch
            {
                return new Random().Next(10, 90); // Fallback
            }
        }

        private float GetGpuUsage()
        {
            // Requires GPU-specific library (e.g., NVAPI for NVIDIA)
            return new Random().Next(5, 70); // Stub
        }

        private float GetAvailableMemory()
        {
            try
            {
                using var counter = new PerformanceCounter("Memory", "Available MBytes");
                return counter.NextValue();
            }
            catch
            {
                return new Random().Next(1000, 8000); // Fallback
            }
        }

        private float GetNetworkSpeed()
        {
            // Use NetworkInterface.GetAllNetworkInterfaces()
            return new Random().Next(10, 500); // Stub
        }

        private string[] GetAvailableFiles()
        {
            return new[] { "log.txt", "data.json" }; // Stub
        }

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
                    return true;
                }
            }
            catch
            {
                model = "GPU detection failed";
                memoryMB = 0;
            }
            return false;
        }
    }
}



