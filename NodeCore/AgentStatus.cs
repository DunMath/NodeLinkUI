using System;
using System.Collections.Generic;
using System.Text.Json;

namespace NodeCore
{
    public class AgentStatus
    {
        public string AgentId { get; set; } = "";
        public DateTime LastHeartbeat { get; set; }
        public bool IsOnline { get; set; }
        public string IpAddress { get; set; } = ""; // Added for NodeGrid

        public float CpuUsagePercent { get; set; }
        public float GpuUsagePercent { get; set; }
        public float MemoryAvailableMB { get; set; }
        public float NetworkMbps { get; set; }

        public bool HasFileAccess { get; set; }
        public string[] AvailableFiles { get; set; } = Array.Empty<string>();

        // 🧠 Performance metrics for scheduling
        public int TaskQueueLength { get; set; }
        public TimeSpan LastTaskDuration { get; set; }
        public float DiskReadMBps { get; set; }
        public float DiskWriteMBps { get; set; }

        // 🟡 Derived property for quick load check
        public bool IsBusy => TaskQueueLength > 0 || CpuUsagePercent > 85 || GpuUsagePercent > 85;

        // 🧩 GPU capability reporting (plug-and-play + future CUDA support)
        public bool HasGpu { get; set; }
        public bool HasCuda { get; set; }
        public string GpuModel { get; set; } = "";
        public int GpuMemoryMB { get; set; }

        // ✅ Serialize to JSON
        public string ToJson()
        {
            return JsonSerializer.Serialize(this);
        }

        // ✅ Deserialize from JSON
        public static AgentStatus FromJson(string json)
        {
            return JsonSerializer.Deserialize<AgentStatus>(json) ?? new AgentStatus();
        }
    }
}



