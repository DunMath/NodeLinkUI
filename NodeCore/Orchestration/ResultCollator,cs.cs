using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace NodeCore.Orchestration
{
    public interface IResultCollator
    {
        void StartJob(string runId, string app, int expectedCount);
        void Accept(ResultEnvelope result);
        bool IsComplete(string runId);
        event Action<string /*app*/, string /*runId*/, IReadOnlyList<ResultEnvelope> /*ordered*/>? JobCompleted;
    }

    public sealed class DefaultResultCollator : IResultCollator
    {
        private sealed class Job
        {
            public string App = "";
            public int Expected;
            public ConcurrentDictionary<int, ResultEnvelope> Results = new();
        }

        private readonly ConcurrentDictionary<string /*runId*/, Job> _jobs = new();

        public event Action<string, string, IReadOnlyList<ResultEnvelope>>? JobCompleted;

        public void StartJob(string runId, string app, int expectedCount)
        {
            _jobs[runId] = new Job { App = app, Expected = expectedCount };
        }

        public void Accept(ResultEnvelope result)
        {
            // find the job by app name (simple heuristic); alternatively pass runId through payload
            foreach (var kv in _jobs)
            {
                var job = kv.Value;
                if (!string.Equals(job.App, result.App, StringComparison.OrdinalIgnoreCase))
                    continue;

                job.Results[result.Seq] = result;

                if (job.Results.Count >= job.Expected)
                {
                    var ordered = job.Results
                        .OrderBy(k => k.Key)
                        .Select(k => k.Value)
                        .ToList()
                        .AsReadOnly();

                    _jobs.TryRemove(kv.Key, out _);
                    JobCompleted?.Invoke(job.App, kv.Key, ordered);
                }
                return;
            }
        }

        public bool IsComplete(string runId) => !_jobs.ContainsKey(runId);
    }
}

