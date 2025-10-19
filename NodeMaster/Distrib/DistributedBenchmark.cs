using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NodeCore;
using NodeCore.Orchestration;

namespace NodeMaster.Distrib
{
    /// <summary>
    /// Fire-and-forget wrapper to run a measurable distributed benchmark
    /// via the existing orchestrator + collator.
    /// Uses the "DemoHash" agent handler (Compute:DemoHash|seq|bytes=...).
    /// </summary>
    public static class DistributedBenchmark
    {
        public static void RunDemoHashBenchmark(
            DefaultOrchestrator orchestrator,
            DefaultResultCollator collator,
            Func<List<AgentStatus>> getAgents,
            Action<string> log,
            int taskCount = 32,
            int bytesPerTask = 2 * 1024 * 1024)
        {
            if (orchestrator is null || collator is null)
            {
                log("Distributed benchmark not ready (orchestrator/collator is null).");
                return;
            }

            var agents = getAgents?.Invoke() ?? new List<AgentStatus>();
            if (agents.Count == 0)
            {
                log("Distributed benchmark aborted — no registered agents.");
                return;
            }

            var runId = Guid.NewGuid().ToString("N");
            var app = "DemoHash";
            var sw = Stopwatch.StartNew();

            void OnJobCompleted(string appName, string completedRunId, IReadOnlyList<ResultEnvelope> ordered)
            {
                if (!string.Equals(completedRunId, runId, StringComparison.OrdinalIgnoreCase))
                    return; // ignore other runs

                sw.Stop();
                log($"Distributed benchmark complete: {appName} run={completedRunId}, " +
                    $"results={ordered.Count}, elapsed={sw.Elapsed.TotalMilliseconds:F0} ms.");

                collator.JobCompleted -= OnJobCompleted; // detach
            }

            // attach completion callback only for this run
            collator.JobCompleted += OnJobCompleted;

            // build N hash-work units
            var units = new List<TaskUnit>(taskCount);
            for (int i = 0; i < taskCount; i++)
                units.Add(new TaskUnit { App = app, Seq = i, Payload = $"bytes={bytesPerTask}" });

            // start collation & submit to orchestrator
            collator.StartJob(runId, app, taskCount);
            orchestrator.Submit(runId, units);

            log($"Distributed benchmark started: {app} x{taskCount} tasks @ {bytesPerTask / 1024} KB " +
                $"across {agents.Count} agent(s). runId={runId}");
        }
    }
}

