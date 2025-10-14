// NodeCore/AgentStatus.cs — v2.0
// Rich agent model with presence, sticky IP/MAC, latency tracking, and JSON helpers.

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace NodeCore
{
    public sealed class AgentStatus : INotifyPropertyChanged
    {
        // Identity / addressing
        private string _agentId = "";
        public string AgentId { get => _agentId; set => Set(ref _agentId, value); }

        private string _ipAddress = "";
        public string IpAddress { get => _ipAddress; set => Set(ref _ipAddress, value); }

        // Sticky addressing for DHCP-lite
        private string _stickyIp = "";
        public string StickyIp { get => _stickyIp; set => Set(ref _stickyIp, value); }

        private string _mac = "";
        public string Mac { get => _mac; set => Set(ref _mac, value); }

        // Registration / health
        private bool _registered;
        public bool Registered { get => _registered; set => Set(ref _registered, value); }

        private bool _isOnline;
        public bool IsOnline { get => _isOnline; set => Set(ref _isOnline, value); }

        private bool _isDegraded;
        public bool IsDegraded { get => _isDegraded; set => Set(ref _isDegraded, value); }

        // Presence timeline
        private DateTime _lastHeartbeat;
        public DateTime LastHeartbeat { get => _lastHeartbeat; set => Set(ref _lastHeartbeat, value); }

        // Public set so Bootstrapper and UI can maintain it
        private DateTime _lastSeen = DateTime.MinValue;
        public DateTime LastSeen { get => _lastSeen; set => Set(ref _lastSeen, value); }

        // Volatile (pulse) metrics
        private float _cpuUsagePercent;
        public float CpuUsagePercent { get => _cpuUsagePercent; set => Set(ref _cpuUsagePercent, value); }

        private float _memoryAvailableMB;
        public float MemoryAvailableMB { get => _memoryAvailableMB; set => Set(ref _memoryAvailableMB, value); }

        private float _gpuUsagePercent;
        public float GpuUsagePercent { get => _gpuUsagePercent; set => Set(ref _gpuUsagePercent, value); }

        private int _taskQueueLength;
        public int TaskQueueLength { get => _taskQueueLength; set => Set(ref _taskQueueLength, value); }

        private float _networkMbps;
        public float NetworkMbps { get => _networkMbps; set => Set(ref _networkMbps, value); }

        private float _diskReadMBps;
        public float DiskReadMBps { get => _diskReadMBps; set => Set(ref _diskReadMBps, value); }

        private float _diskWriteMBps;
        public float DiskWriteMBps { get => _diskWriteMBps; set => Set(ref _diskWriteMBps, value); }

        // Static machine capabilities
        private int _cpuLogicalCores;
        public int CpuLogicalCores { get => _cpuLogicalCores; set => Set(ref _cpuLogicalCores, value); }

        private bool _hasGpu;
        public bool HasGpu { get => _hasGpu; set => Set(ref _hasGpu, value); }

        private bool _hasCuda;
        public bool HasCuda { get => _hasCuda; set => Set(ref _hasCuda, value); }

        private string _gpuModel = "";
        public string GpuModel { get => _gpuModel; set => Set(ref _gpuModel, value); }

        private int _gpuMemoryMB;
        public int GpuMemoryMB { get => _gpuMemoryMB; set => Set(ref _gpuMemoryMB, value); }

        private int _gpuCount;
        public int GpuCount { get => _gpuCount; set => Set(ref _gpuCount, value); }

        private string _instanceId = "";
        public string InstanceId { get => _instanceId; set => Set(ref _instanceId, value); }

        // File access (optional)
        private bool _hasFileAccess;
        public bool HasFileAccess { get => _hasFileAccess; set => Set(ref _hasFileAccess, value); }

        private string[] _availableFiles = Array.Empty<string>();
        public string[] AvailableFiles { get => _availableFiles; set => Set(ref _availableFiles, value); }

        // Self-test and recency of compute
        private string _selfTestStatus = "";   // "✓", "✗", or "…"
        public string SelfTestStatus { get => _selfTestStatus; set => Set(ref _selfTestStatus, value); }

        private string _lastComputeResult = "";
        public string LastComputeResult { get => _lastComputeResult; set => Set(ref _lastComputeResult, value); }

        private DateTime _lastComputeAt;
        public DateTime LastComputeAt { get => _lastComputeAt; set => Set(ref _lastComputeAt, value); }

        private TimeSpan _lastTaskDuration = TimeSpan.Zero;
        public TimeSpan LastTaskDuration { get => _lastTaskDuration; set => Set(ref _lastTaskDuration, value); }

        // UI-only aggregation (bound in XAML)
        private int _softThreadsQueued;
        public int SoftThreadsQueued { get => _softThreadsQueued; set => Set(ref _softThreadsQueued, value); }

        // Latency tracking
        private double _lastLatencyMs;
        public double LastLatencyMs { get => _lastLatencyMs; set => Set(ref _lastLatencyMs, value); }

        private double _avgLatencyMs;
        public double AvgLatencyMs { get => _avgLatencyMs; set => Set(ref _avgLatencyMs, value); }

        private int _samples;
        public int Samples { get => _samples; set => Set(ref _samples, value); }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? prop = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(prop);
            return true;
        }

        // --- Helpers ---

        /// <summary>Update latency with a simple running average (or EMA if alpha provided).</summary>
        public void UpdateLatency(double ms, double? alpha = null)
        {
            LastLatencyMs = ms;
            if (Samples <= 0) { AvgLatencyMs = ms; Samples = 1; return; }

            if (alpha is double a && a > 0 && a <= 1)
                AvgLatencyMs = (a * ms) + ((1 - a) * AvgLatencyMs);
            else
                AvgLatencyMs = ((AvgLatencyMs * Samples) + ms) / (Samples + 1);

            Samples++;
        }
        // NEW: computed busy flag the scheduler can read
        public bool IsBusy
        {
            get
            {
                // Busy if it currently has tasks queued, or it's mid self-test right after registration.
                // Tweak the logic to match your semantics.
                if (TaskQueueLength > 0) return true;
                if (Registered && SelfTestStatus == "…") return true;
                return false;
            }
        }

        // JSON helpers (so Bootstrapper/UI can share a common format)

        public string ToJson(JsonSerializerOptions? options = null)
            => JsonSerializer.Serialize(this, options ?? DefaultJsonOptions);

        public static AgentStatus FromJson(string json, string? ip = null, string? mac = null)
        {
            var model = JsonSerializer.Deserialize<AgentStatus>(json, DefaultJsonOptions) ?? new AgentStatus();
            if (!string.IsNullOrWhiteSpace(ip)) model.IpAddress = ip!;
            if (!string.IsNullOrWhiteSpace(mac)) model.Mac = mac!;
            return model;
        }

        public static readonly JsonSerializerOptions DefaultJsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }
}










