using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NodeCore; // AgentStatus
// Uses TaskUnit / ResultEnvelope from NodeCore.Orchestration.TaskContracts

namespace NodeCore.Orchestration
{
    public interface IOrchestrator
    {
        void Submit(string runId, IReadOnlyList<TaskUnit> tasks);
        void OnResult(ResultEnvelope result);

        // host (SoftDispatcher/comm) must actually send the payload
        event Action<string /*agentId*/, string /*corrId*/, string /*payload*/>? DispatchRequested;
    }

    /// <summary>
    /// Default scheduler:
    ///  - Chooses eligible agents (Registered+Online)
    ///  - Distributes work round-robin (optionally honoring TaskUnit.AffinityAgentId)
    ///  - Keeps a per-agent queue and an in-flight cap (MaxOutstandingPerAgent)
    ///  - Pumps more work for an agent when a result arrives (OnResult)
    ///  - Measures end-to-end RTT on the manager by recording send time and stopping on result
    ///    (now keyed by corrId with legacy fallback to agentId/app/seq)
    /// </summary>
    public sealed class DefaultOrchestrator : IOrchestrator
    {
        private readonly Func<List<AgentStatus>> _snapshotAgents;

        // Per-agent FIFO of (runId, task)
        private readonly ConcurrentDictionary<string, ConcurrentQueue<(string runId, TaskUnit unit)>> _perAgentQueues = new();

        // Tracks how many tasks are currently in-flight per agent (not yet completed)
        private readonly Dictionary<string, int> _inflight = new(StringComparer.OrdinalIgnoreCase);

        // Send timestamp lookup: single store keyed by either corr::<corrId> or seq::<agentId>::<app>::<seq>
        private readonly ConcurrentDictionary<string, DateTime> _sentAtByKey = new();

        // Per-agent EMA of RTT (ms)
        private readonly Dictionary<string, double> _emaMs = new(StringComparer.OrdinalIgnoreCase);

        // Simple lock to protect _inflight, EMA & queue-pumping decisions
        private readonly object _gate = new();

        public event Action<string, string, string>? DispatchRequested;

        /// <summary>
        /// Fired when a latency sample is computed.
        /// Signature kept stable: (agentId, app, seq, ms)
        /// </summary>
        public event Action<string /*agentId*/, string /*app*/, int /*seq*/, double /*ms*/>? LatencyMeasured;

        /// <summary>
        /// How many outstanding tasks we allow per agent at once (default 1).
        /// </summary>
        public int MaxOutstandingPerAgent { get; }

        public DefaultOrchestrator(Func<List<AgentStatus>> snapshotAgents, int maxOutstandingPerAgent = 1)
        {
            _snapshotAgents = snapshotAgents ?? throw new ArgumentNullException(nameof(snapshotAgents));
            if (maxOutstandingPerAgent <= 0) maxOutstandingPerAgent = 1;
            MaxOutstandingPerAgent = maxOutstandingPerAgent;
        }

        public void Submit(string runId, IReadOnlyList<TaskUnit> tasks)
        {
            if (string.IsNullOrWhiteSpace(runId) || tasks == null || tasks.Count == 0)
                return;

            // Eligible agents ordered by current load
            var agents = _snapshotAgents()
                .Where(a => a.Registered && a.IsOnline)
                .OrderBy(a => a.TaskQueueLength)
                .ThenBy(a => a.CpuUsagePercent)
                .ToList();

            if (agents.Count == 0)
                return;

            int rr = 0;

            foreach (var t in tasks)
            {
                // If the task asks for a specific agent, try to honor it
                var target = !string.IsNullOrWhiteSpace(t.AffinityAgentId)
                    ? agents.FirstOrDefault(a => string.Equals(a.AgentId, t.AffinityAgentId, StringComparison.OrdinalIgnoreCase))
                    : null;

                // Otherwise round-robin among eligible agents
                target ??= agents[rr++ % agents.Count];

                // Enqueue to that agent's queue
                var q = _perAgentQueues.GetOrAdd(target.AgentId, _ => new ConcurrentQueue<(string, TaskUnit)>());
                q.Enqueue((runId, t));

                // Try to pump work to this agent now (respecting in-flight cap)
                TryPump(target.AgentId);
            }
        }

        public void OnResult(ResultEnvelope result)
        {
            if (result == null) return;

            string agentId = result.AgentId ?? result.SenderId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(agentId)) return;

            // FIX: initialise t0 so it's definitely assigned
            DateTime t0 = default;
            bool found = false;

            if (!string.IsNullOrWhiteSpace(result.CorrId))
                found = _sentAtByKey.TryRemove(CorrKey(result.CorrId!), out t0);

            if (!found && !string.IsNullOrWhiteSpace(result.App))
                found = _sentAtByKey.TryRemove(SeqKey(agentId, result.App!, result.Seq), out t0);

            if (found)
            {
                var ms = Math.Max(0, (DateTime.UtcNow - t0).TotalMilliseconds);
                lock (_gate)
                {
                    if (_emaMs.TryGetValue(agentId, out var ema))
                        _emaMs[agentId] = ema * 0.8 + ms * 0.2;
                    else
                        _emaMs[agentId] = ms;
                }
                try { LatencyMeasured?.Invoke(agentId, result.App!, result.Seq, ms); } catch { }
                Debug.WriteLine($"[Orchestrator] {agentId} {result.App}|{result.Seq} rtt={ms:n0} ms");
            }

            lock (_gate)
            {
                if (_inflight.TryGetValue(agentId, out var n) && n > 0)
                    _inflight[agentId] = n - 1;
            }

            TryPump(agentId);
        }


        private void TryPump(string agentId)
        {
            // We may send multiple units if the cap allows, so loop.
            while (true)
            {
                (string runId, TaskUnit unit)? dequeued = null;

                lock (_gate)
                {
                    // Respect in-flight cap
                    _inflight.TryGetValue(agentId, out var inFlightNow);
                    if (inFlightNow >= MaxOutstandingPerAgent)
                        break;

                    if (!_perAgentQueues.TryGetValue(agentId, out var q) || !q.TryDequeue(out var item))
                        break; // nothing to send

                    // Reserve a slot for this agent
                    _inflight[agentId] = inFlightNow + 1;
                    dequeued = item;
                }

                // Outside the lock: build corrId & payload and raise DispatchRequested
                var (runId, unit) = dequeued.Value;

                // Correlation id that’s unique and traceable
                var corrId = $"{unit.App}-{unit.Seq}-{Guid.NewGuid():N}";

                // Ensure corrId + sentUtc are embedded on the wire (non-breaking)
                var sentUtc = DateTime.UtcNow;
                unit.CorrId = corrId;
                unit.SentUtc = sentUtc;

                // Convert to wire; host decides how to send (SoftDispatcher / comm)
                var payload = TaskWire.ToWire(unit); // embeds corrId + sentUtc into payload JSON when present

                // Record send timestamp for RTT later; prefer corrId key, keep legacy fallback one liner for old agents
                _sentAtByKey[CorrKey(corrId)] = sentUtc;
                _sentAtByKey[SeqKey(agentId, unit.App, unit.Seq)] = sentUtc; // harmless if agent echoes corrId

                DispatchRequested?.Invoke(agentId, corrId, payload);

                // loop to see if we can issue another immediately (if cap > 1)
            }
        }

        private static string CorrKey(string corrId) => $"corr::{corrId}";
        private static string SeqKey(string agentId, string app, int seq) => $"seq::{agentId}::{app}::{seq}";
    }
}




