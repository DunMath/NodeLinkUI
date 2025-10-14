// NodeMaster/Distrib/MasterOrchestrator.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NodeCore;
using NodeCore.Distrib;
using NodeComm;

namespace NodeMaster.Distrib
{
    public sealed class MasterOrchestrator
    {
        private readonly CommChannel _comm;
        private readonly Func<IEnumerable<AgentStatus>> _getAgents;   // pull live UI list without coupling
        private readonly Action<string> _log;
        private readonly ISoftDispatcher? _soft;                       // your existing dispatcher (nullable)

        private sealed class RunningJob
        {
            public string AppId = "";
            public string CorrId = "";
            public int Parts;
            public byte[] Input = Array.Empty<byte>();
            public DateTime StartUtc;
            public ConcurrentDictionary<int, byte[]> Results = new();
            public ConcurrentDictionary<int, int> PartMs = new();
        }

        private readonly ConcurrentDictionary<string, RunningJob> _jobs = new();

        public MasterOrchestrator(CommChannel comm,
                                  Func<IEnumerable<AgentStatus>> getAgents,
                                  Action<string> log,
                                  ISoftDispatcher? softDispatcher = null)
        {
            _comm = comm;
            _getAgents = getAgents;
            _log = log;
            _soft = softDispatcher;
        }

        public void RunDistributed(string appId, byte[] input, int? parts = null)
        {
            var app = AppCatalog.Get(appId);
            if (app is null) { _log($"App '{appId}' not found."); return; }

            var agents = _getAgents()
                .Where(a => a.Registered && a.IsOnline)
                .Select(a => a.AgentId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (agents.Count == 0) { _log("No agents online."); return; }

            int partCount = parts ?? Math.Max(agents.Count, 1);
            var corr = Guid.NewGuid().ToString("N");
            var job = new RunningJob
            {
                AppId = appId,
                CorrId = corr,
                Parts = partCount,
                Input = input,
                StartUtc = DateTime.UtcNow
            };
            _jobs[corr] = job;

            // Round-robin assign
            int k = 0;
            for (int i = 0; i < partCount; i++)
            {
                var agentId = agents[k++ % agents.Count];
                var payload = app.PartitionJob(input, i, partCount);
                var msg = Wire.BuildAppRun(appId, corr, i, partCount, agentId, payload);

                if (_soft != null) _soft.Enqueue(agentId, $"job-{corr}-{i}", msg);
                else _comm.SendToAgent(agentId, msg);
            }

            _log($"Dispatched {partCount} parts for '{appId}' (corr {corr}) to {agents.Count} agent(s).");
        }

        // Call this from your existing message pump when a line starts with "AppResult:"
        public bool HandleAppResult(string message, string fromAgentId,
            Action<string, double, double, int>? updateLatency = null,
            Action<string, string>? onJobDoneSummary = null)
        {
            if (!message.StartsWith("AppResult:", StringComparison.OrdinalIgnoreCase)) return false;

            var nl = message.IndexOf('\n');
            var header = nl > 0 ? message[..nl] : message;
            var body = nl > 0 ? message[(nl + 1)..] : "";

            var h = header.Split(':', 2)[1].Split('|');
            // AppResult:{AppId}|{CorrId}|{i}/{n}|{OK/FAIL}|{ms}
            string appId = h[0];
            string corr = h[1];
            var idxPair = h[2].Split('/');
            int i = int.Parse(idxPair[0]);
            int n = int.Parse(idxPair[1]);
            string status = h[3];
            int ms = int.Parse(h[4]);

            if (!_jobs.TryGetValue(corr, out var job)) { _log($"Unknown job {corr}"); return true; }
            if (!appId.Equals(job.AppId, StringComparison.OrdinalIgnoreCase)) return true;

            if (!status.Equals("OK", StringComparison.OrdinalIgnoreCase))
            {
                _log($"Part {i}/{n} FAILED from {fromAgentId}: {body}");
                // optional: mark failure / retry
                return true;
            }

            var data = Convert.FromBase64String(body);
            job.Results[i] = data;
            job.PartMs[i] = ms;

            // Push latency up (lets caller update AgentStatus row)
            updateLatency?.Invoke(fromAgentId, ms, Avg(job.PartMs.Values), job.PartMs.Count);

            // If complete → merge
            if (job.Results.Count == job.Parts)
            {
                var app = AppCatalog.Get(job.AppId)!;
                var ordered = Enumerable.Range(0, job.Parts).Select(ii => job.Results[ii]).ToList();
                var final = app.MergeResults(ordered);
                var totalMs = (int)(DateTime.UtcNow - job.StartUtc).TotalMilliseconds;

                _log($"Job {job.AppId} ({corr}) complete in {totalMs} ms; parts={job.Parts}.");
                onJobDoneSummary?.Invoke(job.AppId, $"{job.Parts} parts, {totalMs} ms");

                _jobs.TryRemove(corr, out _);
                // you can do something with 'final' (e.g., display, save)
            }
            return true;
        }

        private static double Avg(IEnumerable<int> xs)
        {
            double s = 0; int c = 0;
            foreach (var v in xs) { s += v; c++; }
            return c == 0 ? 0 : s / c;
        }
    }

    // Minimal abstraction to avoid referencing your internal dispatcher type directly in this file
    public interface ISoftDispatcher
    {
        void Enqueue(string agentId, string id, string payload);
    }
}

