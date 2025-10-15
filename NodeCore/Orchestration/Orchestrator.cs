using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NodeCore; // adjust to your AgentStatus namespace

namespace NodeCore.Orchestration
{
    public interface IOrchestrator
    {
        // Called by UI / job planner:
        void Submit(string runId, IReadOnlyList<TaskUnit> tasks);

        // Called by result path (so we can update scores/latency if needed):
        void OnResult(ResultEnvelope result);

        // Raised whenever a unit is ready to go out; host must actually send it (e.g., via _softDispatcher)
        event Action<string /*agentId*/, string /*corrId*/, string /*payload*/>? DispatchRequested;
    }

    public sealed class DefaultOrchestrator : IOrchestrator
    {
        private readonly Func<List<AgentStatus>> _snapshotAgents;

        // simple queues per agentId
        private readonly ConcurrentDictionary<string, ConcurrentQueue<(string runId, TaskUnit t)>> _perAgentQueues = new();

        public event Action<string, string, string>? DispatchRequested;

        public DefaultOrchestrator(Func<List<AgentStatus>> snapshotAgents)
        {
            _snapshotAgents = snapshotAgents;
        }

        public void Submit(string runId, IReadOnlyList<TaskUnit> tasks)
        {
            if (tasks.Count == 0) return;

            // naive policy: round-robin across best agents by (Registered, IsOnline, lower Cpu and Queue)
            var agents = _snapshotAgents()
                .Where(a => a.Registered && a.IsOnline)
                .OrderBy(a => a.TaskQueueLength) // you can make this smarter with latency/EMA weighting
                .ThenBy(a => a.CpuUsagePercent)
                .ToList();

            if (agents.Count == 0) return;

            int idx = 0;
            foreach (var t in tasks)
            {
                var target = t.AffinityAgentId != null
                    ? agents.FirstOrDefault(a => string.Equals(a.AgentId, t.AffinityAgentId, StringComparison.OrdinalIgnoreCase))
                    : agents[idx++ % agents.Count];

                if (target == null) target = agents[idx++ % agents.Count];

                var q = _perAgentQueues.GetOrAdd(target.AgentId, _ => new ConcurrentQueue<(string, TaskUnit)>());
                q.Enqueue((runId, t));

                // ask host to send immediately (SoftDispatcher will enforce per-agent concurrency)
                var corrId = $"{t.App}-{t.Seq}-{Guid.NewGuid():N}";
                DispatchRequested?.Invoke(target.AgentId, corrId, TaskWire.ToWire(t));
            }
        }

        public void OnResult(ResultEnvelope result)
        {
            // here you could learn from latency, success rate etc.
            // For now we do nothing. If you add per-agent scoring, update it here.
        }
    }
}

