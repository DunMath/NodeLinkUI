using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NodeCore
{
    public class AgentStatus
    {
        public string AgentId { get; set; } = "";
        public DateTime LastHeartbeat { get; set; }
        public bool IsOnline { get; set; }
        public string IpAddress { get; set; } = ""; // For NodeGrid

        public float CpuUsagePercent { get; set; }
        public float GpuUsagePercent { get; set; }
        public float MemoryAvailableMB { get; set; }
        public float NetworkMbps { get; set; }

        public bool HasFileAccess { get; set; }
        public string[] AvailableFiles { get; set; } = Array.Empty<string>();

        // Performance metrics for scheduling
        public int TaskQueueLength { get; set; }
        public TimeSpan LastTaskDuration { get; set; }
        public float DiskReadMBps { get; set; }
        public float DiskWriteMBps { get; set; }

        // SoftThreads capacity input (Agents should set to Environment.ProcessorCount)
        public int CpuLogicalCores { get; set; } = 0;

        // Quick load check
        public bool IsBusy => TaskQueueLength > 0 || CpuUsagePercent > 85 || GpuUsagePercent > 85;

        // GPU capability reporting (single GPU in v1)
        public bool HasGpu { get; set; }
        public bool HasCuda { get; set; }
        public string GpuModel { get; set; } = "";
        public int GpuMemoryMB { get; set; }

        // For Pro upsell (Agent restarts + multi-GPU detection)
        public int GpuCount { get; set; } = 0;        // number of GPUs on the node
        public string InstanceId { get; set; } = "";  // unique per NodeLinkUI process run (GUID recommended)

        // Master-only display: cached soft threads (not serialized)
        [JsonIgnore]
        public int SoftThreadsQueued { get; set; } = 0;

        // Compute test UI indicator (… / ✓ / ✗) – not serialized
        [JsonIgnore]
        public string SelfTestStatus { get; set; } = string.Empty;

        // NEW: row-colour state — becomes true after RegisterAck or AgentConfig on Master
        public bool Registered { get; set; } = false;

        // NEW: derived UI flag for “yellow” state (don’t serialize)
        [JsonIgnore]
        public bool IsDegraded { get; set; } = false;

        // Serialize to JSON (excludes [JsonIgnore] fields)
        public string ToJson() => JsonSerializer.Serialize(this);

        // Deserialize from JSON
        public static AgentStatus FromJson(string json)
            => JsonSerializer.Deserialize<AgentStatus>(json) ?? new AgentStatus();
    }
}







