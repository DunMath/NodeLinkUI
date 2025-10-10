// NodeCore/AgentPulse.cs
using System;

namespace NodeCore
{
    public sealed class AgentPulse
    {
        public string AgentId { get; set; } = "";
        public float CpuUsagePercent { get; set; }
        public float MemoryAvailableMB { get; set; }
        public float GpuUsagePercent { get; set; }
        public int TaskQueueLength { get; set; }
        public float NetworkMbps { get; set; }
        public float DiskReadMBps { get; set; }
        public float DiskWriteMBps { get; set; }
        public DateTime LastHeartbeat { get; set; }
    }
}

