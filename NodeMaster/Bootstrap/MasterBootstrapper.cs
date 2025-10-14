// NodeMaster/Bootstrap/MasterBootstrapper.cs — v1.1
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using NodeCore;
using NodeMaster;
using NodeMaster.State;

namespace NodeMaster
{
    /// <summary>
    /// Central authority on the Master side that:
    ///  - tracks known agents (config + pulses)
    ///  - raises discovery/registration events for the UI
    ///  - maintains sticky IP leases (MAC -> IP) for 6 months
    ///  - records compute round-trip latency (dispatch -> result)
    /// </summary>
    public sealed class MasterBootstrapper
    {
        // === Back-compat static events expected by the UI ===
        // (UI does: MasterBootstrapper.AgentRegistered += ... etc.)
        public static event Action<string>? AgentRegistered;
        public static event Action<AgentStatus>? OnAgentDiscovered;

        // Preferred richer, instance-scoped stream of status updates
        public event Action<AgentStatus>? OnAgentStatus;

        // In-memory agent state
        private readonly Dictionary<string, AgentStatus> _agents = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _everDiscovered = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        // Track compute dispatch times so we can compute round-trip latency at result time
        // Key: (agentId, taskId) -> utc dispatch time
        private readonly ConcurrentDictionary<(string agentId, string taskId), DateTime> _dispatchTimesUtc
            = new();

        // Sticky MAC->IP reservations (6 months)
        private readonly LeaseStore _leases = new(
            Path.Combine(AppContext.BaseDirectory, "state", "leases.json"),
            TimeSpan.FromDays(180));

        // --- Add this static method to match UI usage ---
        public static void Initialize(
            Action<string> log,
            Func<IEnumerable<AgentStatus>> getAgents,
            Action<AgentStatus> updateAgent,
            Func<string> getMasterId,
            Func<string> getMasterIp)
        {
            // You can add initialization logic here if needed.
            log?.Invoke("[MasterBootstrapper] Initialized.");
            // Example: You could wire up static events, load state, etc.
        }
            // ---------- Public API ----------

            /// <summary>
            /// Call this from your comms layer for every inbound payload.
            /// </summary>
        public void HandleInbound(string fromId, string payload)
        {
            if (payload.StartsWith("AgentConfig:", StringComparison.Ordinal))
            {
                var json = payload.AsSpan("AgentConfig:".Length);
                var cfg = JsonSerializer.Deserialize<AgentConfig>(json);
                if (cfg != null) ApplyAgentConfig(cfg, fromId);
                return;
            }

            if (payload.StartsWith("AgentPulse:", StringComparison.Ordinal))
            {
                ApplyAgentPulse(payload.Substring("AgentPulse:".Length), fromId);
                return;
            }

            if (payload.StartsWith("ComputeResult:", StringComparison.Ordinal))
            {
                // Expected form:  ComputeResult:{TaskId}:{Status}
                // If your wire format differs, adapt the parsing below.
                // We'll try to parse TaskId to compute latency.
                try
                {
                    var rest = payload.Substring("ComputeResult:".Length);
                    var sep = rest.IndexOf(':');
                    if (sep > 0)
                    {
                        var taskId = rest.Substring(0, sep);
                        NoteComputeResult(fromId, taskId, DateTime.UtcNow);
                    }
                }
                catch
                {
                    // ignore malformed result; no latency update
                }
                return;
            }

            // other payloads ignored here
        }

        /// <summary>
        /// Master-side: mark an agent as successfully registered (HTTP control plane).
        /// Optionally provide MAC and IP so we can persist a sticky lease.
        /// </summary>
        public void MarkRegistered(string agentId, string? mac, IPAddress? ip)
        {
            lock (_lock)
            {
                var s = GetOrCreate(agentId);
                s.Registered = true;
                s.IsOnline = true;
                s.LastSeen = DateTime.UtcNow;

                if (!string.IsNullOrWhiteSpace(mac))
                {
                    var normMac = LeaseStore.NormalizeMac(mac);
                    if (ip != null)
                    {
                        var lease = _leases.Upsert(normMac, ip, s.AgentId);
                        s.Mac = lease.Mac;
                        s.StickyIp = lease.Ip;
                        s.IpAddress ??= ip.ToString();
                    }
                    else
                    {
                        s.Mac = normMac;
                        var sticky = _leases.TryGetByMac(normMac);
                        if (sticky != null)
                        {
                            s.StickyIp = sticky.Ip;
                            s.IpAddress ??= sticky.Ip;
                        }
                    }
                }

                // UI updates
                OnAgentStatus?.Invoke(Clone(s));
            }

            // Back-compat static event for the existing UI wiring
            AgentRegistered?.Invoke(agentId);
        }

        /// <summary>
        /// Call this at dispatch time so we can compute latency at result time.
        /// </summary>
        public void NoteComputeDispatched(string agentId, string taskId, DateTime utcDispatched)
        {
            _dispatchTimesUtc[(agentId, taskId)] = utcDispatched;
        }

        /// <summary>
        /// Call this when a compute result arrives; updates per-agent latency metrics.
        /// </summary>
        public void NoteComputeResult(string agentId, string taskId, DateTime utcResult)
        {
            if (_dispatchTimesUtc.TryRemove((agentId, taskId), out var sentAt))
            {
                var ms = Math.Max(0, (utcResult - sentAt).TotalMilliseconds);

                lock (_lock)
                {
                    var s = GetOrCreate(agentId);
                    s.LastLatencyMs = ms;

                    // Simple streaming average
                    if (s.Samples <= 0)
                    {
                        s.AvgLatencyMs = ms;
                        s.Samples = 1;
                    }
                    else
                    {
                        s.AvgLatencyMs = (s.AvgLatencyMs * s.Samples + ms) / (s.Samples + 1);
                        s.Samples += 1;
                    }

                    s.SelfTestStatus = "✓"; // treat successful compute as healthy
                    s.IsOnline = true;
                    s.LastSeen = DateTime.UtcNow;

                    OnAgentStatus?.Invoke(Clone(s));
                }
            }
        }

        // ---------- Internal handlers ----------

        private void ApplyAgentConfig(AgentConfig cfg, string fromId)
        {
            lock (_lock)
            {
                var id = cfg.AgentId ?? fromId;
                var isNew = !_agents.ContainsKey(id);

                var s = GetOrCreate(id);
                s.AgentId = id;
                s.IsOnline = true;
                s.LastSeen = DateTime.UtcNow;

                // Hardware snapshot
                s.HasGpu = cfg.HasGpu;
                s.GpuModel = cfg.GpuModel;
                s.GpuMemoryMB = cfg.GpuMemoryMB ?? 0;

                // Prefer reported IP, then sticky if present
                if (!string.IsNullOrWhiteSpace(cfg.Ip))
                    s.IpAddress = cfg.Ip;
                else if (!string.IsNullOrWhiteSpace(s.StickyIp))
                    s.IpAddress ??= s.StickyIp;

                // Sticky lease if we know MAC and any IP
                if (!string.IsNullOrWhiteSpace(cfg.Mac))
                {
                    var mac = LeaseStore.NormalizeMac(cfg.Mac);
                    var ip = ParseIp(s.IpAddress) ?? ParseIp(cfg.Ip);
                    if (ip != null)
                    {
                        var lease = _leases.Upsert(mac, ip, s.AgentId);
                        s.Mac = lease.Mac;
                        s.StickyIp = lease.Ip;
                    }
                    else
                    {
                        s.Mac = mac;
                        var sticky = _leases.TryGetByMac(mac);
                        if (sticky != null) s.StickyIp = sticky.Ip;
                    }
                }

                // Notify UI
                OnAgentStatus?.Invoke(Clone(s));

                // Back-compat "discovered" event (fire once per app session)
                if (isNew && _everDiscovered.Add(id))
                {
                    OnAgentDiscovered?.Invoke(Clone(s));
                }
            }
        }

        private void ApplyAgentPulse(string json, string fromId)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var id = root.GetProperty("AgentId").GetString() ?? fromId;

                // Parse with tolerance (float & int fallbacks)
                float cpu = TryGetSingle(root, "CpuUsagePercent");
                float mem = TryGetSingle(root, "MemoryAvailableMB");
                float gpu = TryGetSingle(root, "GpuUsagePercent");
                int queue = TryGetInt(root, "TaskQueueLength");

                lock (_lock)
                {
                    var isNew = !_agents.ContainsKey(id);
                    var s = GetOrCreate(id);

                    s.CpuUsagePercent = cpu;
                    s.MemoryAvailableMB = mem;
                    s.GpuUsagePercent = gpu;
                    s.TaskQueueLength = queue;

                    s.IsOnline = true;
                    s.LastSeen = DateTime.UtcNow;

                    OnAgentStatus?.Invoke(Clone(s));

                    if (isNew && _everDiscovered.Add(id))
                    {
                        OnAgentDiscovered?.Invoke(Clone(s));
                    }
                }
            }
            catch
            {
                // ignore malformed pulse
            }
        }

        // ---------- Helpers ----------

        private static float TryGetSingle(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var el)) return 0f;
            return el.ValueKind switch
            {
                JsonValueKind.Number when el.TryGetSingle(out var f) => f,
                JsonValueKind.Number when el.TryGetDouble(out var d) => (float)d,
                _ => 0f
            };
        }

        private static int TryGetInt(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var el)) return 0;
            return el.ValueKind switch
            {
                JsonValueKind.Number when el.TryGetInt32(out var i) => i,
                JsonValueKind.Number when el.TryGetDouble(out var d) => (int)d,
                _ => 0
            };
        }

        private static IPAddress? ParseIp(string? ip)
            => IPAddress.TryParse(ip, out var addr) ? addr : null;

        private AgentStatus GetOrCreate(string agentId)
        {
            if (!_agents.TryGetValue(agentId, out var s))
            {
                s = new AgentStatus
                {
                    AgentId = agentId,
                    SelfTestStatus = "…",
                    // defaults for stability
                    CpuUsagePercent = 0,
                    MemoryAvailableMB = 0,
                    GpuUsagePercent = 0,
                    TaskQueueLength = 0
                };
                _agents[agentId] = s;
            }
            return s;
        }

        private static AgentStatus Clone(AgentStatus s) => new()
        {
            AgentId = s.AgentId,
            IpAddress = s.IpAddress,
            StickyIp = s.StickyIp,
            Mac = s.Mac,
            Registered = s.Registered,
            IsOnline = s.IsOnline,
            IsDegraded = s.IsDegraded,
            LastSeen = s.LastSeen,

            CpuUsagePercent = s.CpuUsagePercent,
            MemoryAvailableMB = s.MemoryAvailableMB,

            HasGpu = s.HasGpu,
            GpuModel = s.GpuModel,
            GpuMemoryMB = s.GpuMemoryMB,
            GpuUsagePercent = s.GpuUsagePercent,
            GpuCount = s.GpuCount,            // kept if you add it to AgentStatus
            HasCuda = s.HasCuda,              // kept if you add it to AgentStatus

            TaskQueueLength = s.TaskQueueLength,
            SoftThreadsQueued = s.SoftThreadsQueued,
            SelfTestStatus = s.SelfTestStatus,

            LastLatencyMs = s.LastLatencyMs,
            AvgLatencyMs = s.AvgLatencyMs,
            Samples = s.Samples
        };
    }
}




