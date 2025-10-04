// File: NodeLinkUI/Services/SoftThreadDispatcher.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
// (intentionally not using 'using System.Timers;' to avoid 'Timer' ambiguity)
using NodeCore;

namespace NodeLinkUI.Services
{
    public record SoftThreadEnvelope(string AgentId, string TaskId, string Payload);

    /// <summary>
    /// Master-side soft-thread dispatcher. Queues per-agent work and paces sends
    /// based on current agent load (TaskQueueLength + locally pending sends),
    /// capped by CpuLogicalCores × MaxSoftThreadsPerCore.
    /// </summary>
    public sealed class SoftThreadDispatcher : IDisposable
    {
        // Per-agent outbound queues (payloads waiting to be sent to each agent)
        private readonly ConcurrentDictionary<string, ConcurrentQueue<SoftThreadEnvelope>> _perAgent =
            new(StringComparer.OrdinalIgnoreCase);

        // Locally pending (already sent, awaiting completion) count per agent
        private readonly ConcurrentDictionary<string, int> _pendingLocal =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Func<string, string, bool> _send;   // (payload, agentId) => ok?
        private readonly Func<List<AgentStatus>> _getStatuses;
        private readonly NodeLinkSettings _settings;
        private readonly Action<string> _log;

        // Fully-qualified to avoid ambiguity with System.Threading.Timer
        private readonly System.Timers.Timer _flushTimer;

        public SoftThreadDispatcher(
            Func<string, string, bool> sendFunc,
            Func<List<AgentStatus>> getStatuses,
            NodeLinkSettings settings,
            Action<string> log,
            double flushIntervalMs = 60)
        {
            _send = sendFunc ?? throw new ArgumentNullException(nameof(sendFunc));
            _getStatuses = getStatuses ?? throw new ArgumentNullException(nameof(getStatuses));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _log = log ?? (_ => { });

            _flushTimer = new System.Timers.Timer(flushIntervalMs) { AutoReset = true };
            _flushTimer.Elapsed += (_, __) => TryFlushAll();
            _flushTimer.Start();
        }

        /// <summary>
        /// Queue a payload for a specific agent.
        /// </summary>
        public void Enqueue(string agentId, string taskId, string payload)
        {
            if (string.IsNullOrWhiteSpace(agentId)) return;
            var q = _perAgent.GetOrAdd(agentId, _ => new ConcurrentQueue<SoftThreadEnvelope>());
            q.Enqueue(new SoftThreadEnvelope(agentId, taskId, payload));
        }

        /// <summary>
        /// Called when the master receives a completion/result from an agent.
        /// Decreases the local pending count so pacing can send more.
        /// </summary>
        public void OnResultReceived(string agentId)
        {
            if (string.IsNullOrWhiteSpace(agentId)) return;
            _pendingLocal.AddOrUpdate(agentId, 0, (_, cur) => Math.Max(0, cur - 1));
        }

        /// <summary>
        /// Returns global totals (sum across all agents).
        /// </summary>
        public (int Queued, int PendingLocal) GetTotals()
        {
            int queued = 0;
            foreach (var kv in _perAgent) queued += kv.Value.Count;

            int pendingLocal = 0;
            foreach (var kv in _pendingLocal) pendingLocal += kv.Value;

            return (queued, pendingLocal);
        }

        /// <summary>
        /// Snapshot per-agent counts so the UI can show "cached" SoftThreads in the grid.
        /// Cached = queued (not yet sent) + pending (sent but not yet completed).
        /// </summary>
        public IReadOnlyDictionary<string, (int Queued, int Pending)> SnapshotPerAgent()
        {
            var result = new Dictionary<string, (int Queued, int Pending)>(StringComparer.OrdinalIgnoreCase);

            // queued
            foreach (var kv in _perAgent)
                result[kv.Key] = (kv.Value.Count, 0);

            // pending
            foreach (var kv in _pendingLocal)
            {
                if (result.TryGetValue(kv.Key, out var cur))
                    result[kv.Key] = (cur.Queued, kv.Value);
                else
                    result[kv.Key] = (0, kv.Value);
            }

            return result;
        }

        /// <summary>
        /// Periodically attempts to send small bursts to agents that have capacity.
        /// </summary>
        private void TryFlushAll()
        {
            List<AgentStatus> statuses;
            try { statuses = _getStatuses() ?? new List<AgentStatus>(); }
            catch { return; }

            var map = statuses.ToDictionary(s => s.AgentId, StringComparer.OrdinalIgnoreCase);

            foreach (var kv in _perAgent)
            {
                var agentId = kv.Key;
                var queue = kv.Value;

                if (!map.TryGetValue(agentId, out var s)) continue;
                if (!s.IsOnline) continue;

                int cores = s.CpuLogicalCores > 0 ? s.CpuLogicalCores : 4;
                int maxSoft = Math.Max(1, _settings.MaxSoftThreadsPerCore * cores);

                _pendingLocal.TryGetValue(agentId, out var pending);
                int effective = s.TaskQueueLength + pending;
                if (effective >= maxSoft) continue;

                int budget = Math.Min(3, maxSoft - effective); // small burst
                int sent = 0;

                while (sent < budget && queue.TryDequeue(out var env))
                {
                    if (_send(env.Payload, agentId))
                    {
                        _pendingLocal.AddOrUpdate(agentId, 1, (_, cur) => cur + 1);
                        sent++;
                    }
                    else
                    {
                        // Could not send; put it back and stop trying this cycle
                        queue.Enqueue(env);
                        break;
                    }
                }
            }
        }

        public void Dispose()
        {
            _flushTimer?.Stop();
            _flushTimer?.Dispose();
        }
    }
}


