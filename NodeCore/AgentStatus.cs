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
        public bool HasGpu { get; set; }               // True if any GPU is detected
        public bool HasCuda { get; set; }              // Reserved for future CUDA detection
        public string GpuModel { get; set; } = "";     // GPU name/model
        public int GpuMemoryMB { get; set; }           // Approximate VRAM in MB

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



