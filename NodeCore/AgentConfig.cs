// NodeCore/AgentConfig.cs
namespace NodeCore
{
    public sealed class AgentConfig
    {
        public string AgentId { get; set; } = "";
        public int CpuLogicalCores { get; set; }
        public float RamTotalMB { get; set; }
        public bool HasGpu { get; set; }
        public string? GpuModel { get; set; }
        public int? GpuMemoryMB { get; set; }
        public int? GpuCount { get; set; }
        public string? OsVersion { get; set; }
        public string? InstanceId { get; set; }

        // NEW: for sticky reservation & fast relink
        public string? Mac { get; set; }
        public string? Ip { get; set; }
    }
}



