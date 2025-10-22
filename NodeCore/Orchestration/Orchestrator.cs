// NodeCore/Orchestration/Orchestrator.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NodeCore;                     // AgentStatus
using NodeCore.Orchestration;       // TaskUnit (from TaskContracts) if present

namespace NodeCore.Orchestration
{
    /// <summary>
    /// Manager-side timing core (metrics-first).
    /// - EnqueueStamp(...) optionally marks enqueue time per (agent, app, seq).
    /// - MarkSentNow(...) must be called immediately before the actual socket write
    ///   (stops QueueWait, starts RTT).
    /// - OnResult(...) is called when a result/part arrives (stops RTT).
    /// Emits QueueWaitMeasured (enqueue->send) and LatencyMeasured (send->result).
    /// 
    /// Back-compat:
    /// - DispatchRequested event kept (obsolete) so existing UI can still perform the send.
    /// - Submit(app,tasks) shim retained to avoid breaking callers; it raises DispatchRequested.
    /// </summary>
    public interface IOrchestrator
    {
        // Metrics (manager-measured)
        event Action<string /*agentId*/, string /*app*/, int /*seq*/, double /*rttMs*/>? LatencyMeasured;
        event Action<string /*agentId*/, string /*app*/, int /*seq*/, double /*queueMs*/>? QueueWaitMeasured;

        // Called immediately BEFORE the actual write to the agent.
        void MarkSentNow(string agentId, string corrIdOrNull, string? wirePayload = null);

        // Called when a result/part envelope is accepted by the manager.
        void OnResult(ResultEnvelope result);
    }

    /// <summary>
    /// DefaultOrchestrator: single source of truth for QueueWait/RTT on the manager.
    /// Now metrics-first; dispatch path is raised via obsolete DispatchRequested for UI compatibility.
    /// </summary>
    public sealed class DefaultOrchestrator : IOrchestrator
    {
        // Optional snapshot (kept so ctor signature remains compatible)
        private readonly Func<List<AgentStatus>> _snapshotAgents;

        // enqueue → actual send (QueueWait), keyed by seq::<agentId>::<app>::<seq>
        private readonly ConcurrentDictionary<string, DateTime> _enqueuedAtBySeqKey = new();

        // actual send → result (RTT). Keys: corr::<corrId> OR seq::<agentId>::<app>::<seq>
        private readonly ConcurrentDictionary<string, DateTime> _sentAtByKey = new();

        // Per-agent RTT EWMA (for later scheduling decisions)
        private readonly Dictionary<string, double> _emaMs = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _emaGate = new();

        public event Action<string, string, int, double>? LatencyMeasured;
        public event Action<string, string, int, double>? QueueWaitMeasured;

        /// <summary>
        /// Back-compat: the UI may still be subscribed to this to perform the actual send.
        /// Prefer to call MarkSentNow(...) from the real send site and remove this event later.
        /// </summary>
        [Obsolete("Use direct send with MarkSentNow(...) instead; this is kept for UI compatibility.")]
        public event Action<string /*agentId*/, string /*corrId*/, string /*payload*/>? DispatchRequested;

        // Kept for signature compatibility; not used for metrics.
        public int MaxOutstandingPerAgent { get; }

        public DefaultOrchestrator(Func<List<AgentStatus>> snapshotAgents, int maxOutstandingPerAgent = 1)
        {
            if (snapshotAgents == null) throw new ArgumentNullException(nameof(snapshotAgents));
            _snapshotAgents = snapshotAgents;
            MaxOutstandingPerAgent = Math.Max(1, maxOutstandingPerAgent);
        }

        /// <summary>
        /// Optional: stamp the enqueue moment so QueueWait can be measured (enqueue → actual send).
        /// If not stamped, QueueWait will emit 0 when MarkSentNow fires.
        /// </summary>
        public void EnqueueStamp(string agentId, string app, int seq)
        {
            if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(app)) return;
            _enqueuedAtBySeqKey[SeqKey(agentId, app, seq)] = DateTime.UtcNow;
        }

        /// <summary>
        /// Must be called immediately before the wire write to the agent.
        /// - Stops QueueWait (if stamped) and emits QueueWaitMeasured.
        /// - Starts RTT (by corrId if present; also by seq as fallback if we can parse app/seq).
        /// </summary>
        public void MarkSentNow(string agentId, string corrIdOrNull, string? wirePayload = null)
        {
            var now = DateTime.UtcNow;

            // Start RTT (corrId if present)
            if (!string.IsNullOrWhiteSpace(corrIdOrNull))
                _sentAtByKey[CorrKey(corrIdOrNull!)] = now;

            // If we can parse app/seq from the outgoing payload, also:
            //  - emit QueueWait, and
            //  - start RTT keyed by seq as a fallback (in case CorrId is not echoed).
            if (!string.IsNullOrWhiteSpace(wirePayload) &&
                TryParseAppSeqFromWire(wirePayload!, out var app, out var seq))
            {
                var skey = SeqKey(agentId, app, seq);

                double qms = 0;
                if (_enqueuedAtBySeqKey.TryRemove(skey, out var tEnq))
                    qms = Math.Max(0, (now - tEnq).TotalMilliseconds);

                // Always emit a QueueWait sample (0 if no enqueue recorded) to keep UI fresh.
                try { QueueWaitMeasured?.Invoke(agentId, app, seq, qms); } catch { }
                Debug.WriteLine($"[QueueWait] {agentId} {app}|{seq} = {qms:n0} ms");

                // Track RTT by seq as a backup in case corrId is absent on result.
                _sentAtByKey[skey] = now;
            }
        }

        /// <summary>
        /// Stop RTT on result; also updates per-agent EWMA. Emits LatencyMeasured.
        /// </summary>
        public void OnResult(ResultEnvelope result)
        {
            if (result == null) return;

            string agentId = result.AgentId ?? result.SenderId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(agentId)) return;

            // Complete RTT
            DateTime t0 = default;
            bool found = false;

            if (!string.IsNullOrWhiteSpace(result.CorrId))
                found = _sentAtByKey.TryRemove(CorrKey(result.CorrId!), out t0);

            if (!found && !string.IsNullOrWhiteSpace(result.App))
                found = _sentAtByKey.TryRemove(SeqKey(agentId, result.App!, result.Seq), out t0);

            if (found)
            {
                var ms = Math.Max(0, (DateTime.UtcNow - t0).TotalMilliseconds);

                lock (_emaGate)
                {
                    if (_emaMs.TryGetValue(agentId, out var ema))
                        _emaMs[agentId] = ema * 0.8 + ms * 0.2;
                    else
                        _emaMs[agentId] = ms;
                }

                try { LatencyMeasured?.Invoke(agentId, result.App!, result.Seq, ms); } catch { }
                Debug.WriteLine($"[RTT] {agentId} {result.App}|{result.Seq} = {ms:n0} ms");
            }
        }

        /// <summary>
        /// UI/back-compat shim: keep older callers happy while we migrate dispatch.
        /// It stamps QueueWait start and raises DispatchRequested with a single runId.
        /// NOTE: TaskUnit does NOT carry AgentId; we pick an agent from the snapshot (simple RR).
        /// </summary>
        public void Submit(string app, IReadOnlyList<TaskUnit> tasks)
        {
            if (string.IsNullOrWhiteSpace(app) || tasks == null || tasks.Count == 0) return;

            // One correlation per submit (legacy behavior)
            string runId = Guid.NewGuid().ToString("N");

            var agents = SafeSnapshotAgents();
            if (agents.Count == 0) return;

            foreach (var t in tasks)
            {
                var agent = PickAgent(agents);
                var agentId = agent.AgentId;

                // record enqueue time for QueueWait (keyed by agent+app+seq)
                _enqueuedAtBySeqKey[SeqKey(agentId, app, t.Seq)] = DateTime.UtcNow;

                // build legacy wire payload so existing agents still respond
                // (Compute path remains as a compatibility test harness)
                string payload = $"Compute:{app}|{t.Seq}|{t.Payload ?? ""}";

                try { DispatchRequested?.Invoke(agentId, runId, payload); } catch { }
            }
        }

        // --------- helpers ---------

        private static string CorrKey(string corrId) => $"corr::{corrId}";
        private static string SeqKey(string agentId, string app, int seq) => $"seq::{agentId}::{app}::{seq}";

        // Parses both "AppRun:App|Seq|..." and "Compute:App|Seq|..." to extract app+seq from the wire.
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

        /// <summary>
        /// Optional accessor if you want to display per-agent EWMA RTT in the UI later.
        /// </summary>
        public double? TryGetAgentEwmaMs(string agentId)
        {
            if (string.IsNullOrWhiteSpace(agentId)) return null;
            lock (_emaGate) return _emaMs.TryGetValue(agentId, out var v) ? v : (double?)null;
        }

        // ---- helpers for Submit() ----

        private List<AgentStatus> SafeSnapshotAgents()
        {
            try
            {
                var list = _snapshotAgents?.Invoke() ?? new List<AgentStatus>();
                return list.Where(a => !string.IsNullOrWhiteSpace(a.AgentId)).ToList();
            }
            catch { return new List<AgentStatus>(); }
        }

        private int _rrIndex = 0;

        private AgentStatus PickAgent(List<AgentStatus> agents)
        {
            // Prefer online non-Master; fallback to any agent in the list
            var pool = agents.Where(a => a.IsOnline && !string.Equals(a.AgentId, "Master", StringComparison.OrdinalIgnoreCase)).ToList();
            if (pool.Count == 0) pool = agents;
            if (pool.Count == 0) throw new InvalidOperationException("No agents available");

            var idx = Math.Abs(Interlocked.Increment(ref _rrIndex));
            return pool[idx % pool.Count];
        }
    }
}











