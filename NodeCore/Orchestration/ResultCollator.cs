using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

// Force Timer to mean System.Timers.Timer to avoid ambiguity
using System.Timers;
using Timer = System.Timers.Timer;

namespace NodeCore.Orchestration
{
    public interface IResultCollator
    {
        /// <summary>Declare a new job and how many results to expect. If timeout is provided, a JobTimeout event is raised with whatever results arrived.</summary>
        void StartJob(string runId, string app, int expectedCount, TimeSpan? timeout = null);

        /// <summary>Feed one result envelope into the collator.</summary>
        void Accept(ResultEnvelope result);

        /// <summary>True if the job was completed or removed (no longer tracked).</summary>
        bool IsComplete(string runId);

        /// <summary>Try to get progress snapshot for a job.</summary>
        bool TryGetProgress(string runId, out int received, out int expected);

        /// <summary>Cancel the job and drop any partial results (optionally still reported via JobTimeout for a single place to listen).</summary>
        void CancelJob(string runId, string reason = "cancelled");

        /// <summary>Fired when a job collected all expected results. Gives ordered results by Seq.</summary>
        event Action<string /*app*/, string /*runId*/, IReadOnlyList<ResultEnvelope> /*ordered*/>? JobCompleted;

        /// <summary>Fired when a job times out or is cancelled, with any partial ordered results.</summary>
        event Action<string /*app*/, string /*runId*/, string /*reason*/, IReadOnlyList<ResultEnvelope> /*partialOrdered*/>? JobTimeout;
    }

    public sealed class DefaultResultCollator : IResultCollator
    {
        private sealed class Job
        {
            public string App = "";
            public int Expected;
            public ConcurrentDictionary<int, ResultEnvelope> Results = new();
            public DateTime StartedUtc = DateTime.UtcNow;
            public DateTime? DeadlineUtc;
            public string? CancelReason;
        }

        private readonly ConcurrentDictionary<string /*runId*/, Job> _jobs = new();

        // one lightweight housekeeping timer (lazy started) for timeouts
        private readonly object _timerLock = new();
        private Timer? _timer;

        public event Action<string, string, IReadOnlyList<ResultEnvelope>>? JobCompleted;
        public event Action<string, string, string, IReadOnlyList<ResultEnvelope>>? JobTimeout;

        public void StartJob(string runId, string app, int expectedCount, TimeSpan? timeout = null)
        {
            if (string.IsNullOrWhiteSpace(runId))
                throw new ArgumentException("runId is required", nameof(runId));

            var job = new Job
            {
                App = app ?? "",
                Expected = Math.Max(0, expectedCount),
                StartedUtc = DateTime.UtcNow,
                DeadlineUtc = timeout.HasValue ? DateTime.UtcNow + timeout.Value : null
            };

            _jobs[runId] = job;

            if (timeout.HasValue)
                EnsureTimer();
        }

        public void Accept(ResultEnvelope result)
        {
            if (result == null)
                return;

            // Prefer runId when present & tracked
            Job? job = null;
            string? boundRunId = null;

            if (!string.IsNullOrWhiteSpace(result.RunId) && _jobs.TryGetValue(result.RunId, out var exact))
            {
                job = exact;
                boundRunId = result.RunId;
            }
            else
            {
                // Heuristic fallback: newest job matching App (useful for quick tests)
                Job? best = null;
                string? bestRunId = null;

                foreach (var kv in _jobs)
                {
                    var j = kv.Value;
                    if (string.Equals(j.App, result.App, StringComparison.OrdinalIgnoreCase))
                    {
                        if (best == null || j.StartedUtc > best.StartedUtc)
                        {
                            best = j;
                            bestRunId = kv.Key;
                        }
                    }
                }

                job = best;
                boundRunId = bestRunId;
            }

            if (job == null)
                return; // stray or late result

            job.Results[result.Seq] = result;

            if (job.Expected > 0 && job.Results.Count >= job.Expected)
            {
                CompleteAndFire(job, boundRunId);
            }
        }

        public bool IsComplete(string runId) => !_jobs.ContainsKey(runId);

        public bool TryGetProgress(string runId, out int received, out int expected)
        {
            received = 0;
            expected = 0;

            if (_jobs.TryGetValue(runId, out var job))
            {
                expected = job.Expected;
                received = job.Results.Count;
                return true;
            }
            return false;
        }

        public void CancelJob(string runId, string reason = "cancelled")
        {
            if (_jobs.TryRemove(runId, out var job))
            {
                job.CancelReason = reason;
                var ordered = Order(job);
                JobTimeout?.Invoke(job.App, runId, reason, ordered);
            }
        }

        // --- internals ---

        private static IReadOnlyList<ResultEnvelope> Order(Job job) =>
            job.Results
               .OrderBy(kv => kv.Key)
               .Select(kv => kv.Value)
               .ToList()
               .AsReadOnly();

        private void CompleteAndFire(Job job, string? runIdFromLookup)
        {
            // locate the runId key for this job instance if not provided
            string? runId = runIdFromLookup;
            if (runId == null)
            {
                foreach (var kv in _jobs)
                {
                    if (ReferenceEquals(kv.Value, job))
                    {
                        runId = kv.Key;
                        break;
                    }
                }
            }

            if (runId != null && _jobs.TryRemove(runId, out _))
            {
                var ordered = Order(job);
                JobCompleted?.Invoke(job.App, runId, ordered);
            }
        }

        // --- timeout housekeeping ---

        private void EnsureTimer()
        {
            lock (_timerLock)
            {
                if (_timer != null) return;

                _timer = new Timer(500); // 2Hz sweep; cheap
                _timer.AutoReset = true;
                _timer.Elapsed += (_, __) => SweepTimeouts();
                _timer.Start();
            }
        }

        private void SweepTimeouts()
        {
            if (_jobs.IsEmpty) return;

            var now = DateTime.UtcNow;
            var expired = new List<(string runId, Job job)>();

            foreach (var kv in _jobs)
            {
                var j = kv.Value;
                if (j.DeadlineUtc.HasValue && j.DeadlineUtc.Value <= now)
                    expired.Add((kv.Key, j));
            }

            foreach (var (runId, job) in expired)
            {
                if (_jobs.TryRemove(runId, out _))
                {
                    var ordered = Order(job);
                    var reason = job.CancelReason ?? "timeout";
                    JobTimeout?.Invoke(job.App, runId, reason, ordered);
                }
            }

            // stop timer if nothing left with deadlines
            if (_jobs.Values.All(j => !j.DeadlineUtc.HasValue))
            {
                lock (_timerLock)
                {
                    _timer?.Stop();
                    _timer?.Dispose();
                    _timer = null;
                }
            }
        }
    }
}



