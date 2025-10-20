using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NodeCore; // AgentStatus
// Uses TaskUnit / ResultEnvelope / TaskWire from NodeCore.Orchestration.TaskContracts

namespace NodeCore.Orchestration
{
    public interface IOrchestrator
    {
        // Submit work and feed results back
        void Submit(string runId, IReadOnlyList<TaskUnit> tasks);
        void OnResult(ResultEnvelope result);

        // Host (existing path) must actually deliver the payload to the agent
        event Action<string /*agentId*/, string /*corrId*/, string /*payload*/>? DispatchRequested;

        // P0 metrics (manager-measured):
        //   RTT = dispatch-now -> result received
        event Action<string /*agentId*/, string /*app*/, int /*seq*/, double /*rttMs*/>? LatencyMeasured;
        //   QueueWait = enqueue -> dispatch-now
        event Action<string /*agentId*/, string /*app*/, int /*seq*/, double /*queueMs*/>? QueueWaitMeasured;

        // P0: invoked at the moment we consider the unit "sent now".
        // In P0 this is called inside TryPump (just before DispatchRequested).
        // In P1 you can move the call to the actual socket write site.
        void MarkSentNow(string agentId, string corrIdOrNull, string? wirePayload = null);
    }

    /// <summary>
    /// Default scheduler:
    ///  - Chooses eligible agents (Registered + Online)
    ///  - Distributes work (affinity if set; else round-robin)
    ///  - Per-agent queue + in-flight cap
    ///  - Pumps when results arrive
    ///  - P0: measures QueueWait (enqueue->dispatch-now) and RTT (dispatch-now->result) on the manager
    /// </summary>
    public sealed class DefaultOrchestrator : IOrchestrator
    {
        private readonly Func<List<AgentStatus>> _snapshotAgents;

        // Per-agent FIFO of (runId, task)
        private readonly ConcurrentDictionary<string, ConcurrentQueue<(string runId, TaskUnit unit)>> _perAgentQueues = new();

        // Tracks how many tasks are in-flight per agent
        private readonly Dictionary<string, int> _inflight = new(StringComparer.OrdinalIgnoreCase);

        // ================= P0 metrics state =================

        // enqueue -> dispatch-now dwell (manager-side), keyed by seq::<agentId>::<app>::<seq>
        private readonly ConcurrentDictionary<string, DateTime> _enqueuedAtBySeqKey = new();

        // dispatch-now -> result (RTT). Keys: corr::<corrId> or seq::<agentId>::<app>::<seq>
        private readonly ConcurrentDictionary<string, DateTime> _sentAtByKey = new();

        // Per-agent EWMA of RTT
        private readonly Dictionary<string, double> _emaMs = new(StringComparer.OrdinalIgnoreCase);

        // Gate for inflight/ema decisions
        private readonly object _gate = new();

        // ================= Events =================

        public event Action<string, string, string>? DispatchRequested;
        public event Action<string, string, int, double>? LatencyMeasured;
        public event Action<string, string, int, double>? QueueWaitMeasured;

        /// <summary>How many outstanding tasks we allow per agent (default 1).</summary>
        public int MaxOutstandingPerAgent { get; }

        public DefaultOrchestrator(Func<List<AgentStatus>> snapshotAgents, int maxOutstandingPerAgent = 1)
        {
            _snapshotAgents = snapshotAgents ?? throw new ArgumentNullException(nameof(snapshotAgents));
            if (maxOutstandingPerAgent <= 0) maxOutstandingPerAgent = 1;
            MaxOutstandingPerAgent = maxOutstandingPerAgent;
        }

        // ================= Public API =================

        public void Submit(string runId, IReadOnlyList<TaskUnit> tasks)
        {
            if (string.IsNullOrWhiteSpace(runId) || tasks == null || tasks.Count == 0)
                return;

            // Eligible agents ordered by light load signals
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
                // Honor affinity if provided
                var target = !string.IsNullOrWhiteSpace(t.AffinityAgentId)
                    ? agents.FirstOrDefault(a => string.Equals(a.AgentId, t.AffinityAgentId, StringComparison.OrdinalIgnoreCase))
                    : null;

                // Otherwise round-robin
                target ??= agents[rr++ % agents.Count];

                // Enqueue to that agent
                var q = _perAgentQueues.GetOrAdd(target.AgentId, _ => new ConcurrentQueue<(string, TaskUnit)>());
                q.Enqueue((runId, t));

                // P0: remember when we enqueued (for QueueWait)
                _enqueuedAtBySeqKey[SeqKey(target.AgentId, t.App, t.Seq)] = DateTime.UtcNow;

                // Try to pump to this agent (respecting cap)
                TryPump(target.AgentId);
            }
        }

        public void OnResult(ResultEnvelope result)
        {
            if (result == null) return;

            string agentId = result.AgentId ?? result.SenderId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(agentId)) return;

            // Robust t0 extraction (corrId preferred; seq-key fallback)
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
                Debug.WriteLine($"[RTT] {agentId} {result.App}|{result.Seq} = {ms:n0} ms");
            }

            // Free one slot and continue pumping
            lock (_gate)
            {
                if (_inflight.TryGetValue(agentId, out var n) && n > 0)
                    _inflight[agentId] = n - 1;
            }

            TryPump(agentId);
        }

        /// <summary>
        /// P0: mark the *dispatch-now* moment and compute QueueWait if possible.
        /// In P1, move this call to the actual socket write to exclude any remaining dwell.
        /// </summary>
        public void MarkSentNow(string agentId, string corrIdOrNull, string? wirePayload = null)
        {
            var now = DateTime.UtcNow;

            if (!string.IsNullOrWhiteSpace(corrIdOrNull))
                _sentAtByKey[CorrKey(corrIdOrNull!)] = now;

            // If we can parse App|Seq from the wire, compute QueueWait and store seq-key send time too
            if (!string.IsNullOrWhiteSpace(wirePayload) &&
                TryParseAppSeqFromWire(wirePayload!, out var app, out var seq))
            {
                var skey = SeqKey(agentId, app, seq);

                // QueueWait = enqueue -> dispatch-now
                if (_enqueuedAtBySeqKey.TryRemove(skey, out var tEnq))
                {
                    var qms = Math.Max(0, (now - tEnq).TotalMilliseconds);
                    try { QueueWaitMeasured?.Invoke(agentId, app, seq, qms); } catch { }
                    Debug.WriteLine($"[QueueWait] {agentId} {app}|{seq} = {qms:n0} ms");
                }

                // Also store send start by seq-key (legacy corrId-less agents)
                _sentAtByKey[skey] = now;
            }
        }

        // ================= Internals =================

        private void TryPump(string agentId)
        {
            // We may send multiple units if cap allows
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

                    // Reserve a slot
                    _inflight[agentId] = inFlightNow + 1;
                    dequeued = item;
                }

                // Build wire outside the lock
                var (runId, unit) = dequeued.Value;

                // Unique correlation id per dispatch
                var corrId = $"{unit.App}-{unit.Seq}-{Guid.NewGuid():N}";
                var sentUtc = DateTime.UtcNow; // informational; RTT start is below

                unit.CorrId = corrId;
                unit.SentUtc = sentUtc;

                // Wire shape stays backward-compatible (Compute:App|Seq|...)
                var payload = TaskWire.ToWire(unit);

                // P0: treat THIS as "dispatch-now" (start RTT + emit QueueWait)
                MarkSentNow(agentId, corrId, payload);

                // Existing pathway: host delivers the payload (whatever threads/queues exist today)
                try { DispatchRequested?.Invoke(agentId, corrId, payload); } catch { }

                // Loop again if cap > 1
            }
        }

        private static string CorrKey(string corrId) => $"corr::{corrId}";
        private static string SeqKey(string agentId, string app, int seq) => $"seq::{agentId}::{app}::{seq}";

        // Parses "Compute:App|Seq|..." to extract app+seq from the wire
        private static bool TryParseAppSeqFromWire(string wire, out string app, out int seq)
        {
            app = ""; seq = 0;
            if (string.IsNullOrWhiteSpace(wire)) return false;

            var colon = wire.IndexOf(':');
            if (colon <= 0 || colon >= wire.Length - 1) return false;

            var body = wire.Substring(colon + 1); // "App|Seq|payload..."
            var parts = body.Split('|');
            if (parts.Length >= 2 && int.TryParse(parts[1], out seq))
            {
                app = parts[0];
                return !string.IsNullOrWhiteSpace(app);
            }
            return false;
        }
    }
}






