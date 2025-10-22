// NodeMaster/Distrib/MasterOrchestrator.cs  — P1 + no-reply ban guard
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NodeCore;
using NodeCore.Distrib;
using NodeComm;
using NodeCore.Orchestration; // ResultEnvelope

namespace NodeMaster.Distrib
{
    /// <summary>
    /// Lean master-side coordinator for "app/run" distributed jobs.
    /// - Direct synchronous send via CommChannel (no extra queues)
    /// - Partition/merge via AppCatalog apps
    /// - Emits timing hooks for manager-side QueueWait/RTT metrics
    /// - Adds a no-reply timeout: if an agent doesn't reply within T, ban temporarily
    /// </summary>
    public sealed class MasterOrchestrator
    {
        private readonly CommChannel _comm;
        private readonly Func<IEnumerable<AgentStatus>> _getAgents;   // pull live UI list without coupling
        private readonly Action<string> _log;

        /// <summary>
        /// Hook: called immediately BEFORE the actual send to an agent.
        /// Use to timestamp true send moment (QueueWait stop, RTT start).
        /// </summary>
        public Action<string/*agentId*/, string/*corrId*/, string/*wire*/>? OnWillSend { get; set; }

        /// <summary>
        /// Hook: called when a part result is accepted.
        /// Use to close RTT timing in the manager metrics.
        /// </summary>
        public Action<ResultEnvelope>? OnResultForMetrics { get; set; }

        /// <summary>
        /// Notify UI (orchestrator host) when an agent is banned/unbanned due to no-reply.
        /// </summary>
        public Action<string /*agentId*/, bool /*isDegraded*/>? OnAgentBanChanged { get; set; }

        // ----- No-reply guard state -----
        // If an agent is silent after a send for NoReplyTimeout, we mark it banned until Now+BanCooldown.
        // Any result or heartbeat/pulse clears the ban immediately.
        private readonly TimeSpan _noReplyTimeout = TimeSpan.FromSeconds(2.0);
        private readonly TimeSpan _banCooldown = TimeSpan.FromSeconds(10.0);
        private readonly ConcurrentDictionary<string, DateTime> _banUntil =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _lastActivityUtc =
            new(StringComparer.OrdinalIgnoreCase);

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
                                  Action<string> log)
        {
            _comm = comm ?? throw new ArgumentNullException(nameof(comm));
            _getAgents = getAgents ?? throw new ArgumentNullException(nameof(getAgents));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// Partition and dispatch a distributed app job (direct send).
        /// Skips agents currently banned for no-reply; if all are banned, falls back to all.
        /// </summary>
        public void RunDistributed(string appId, byte[] input, int? parts = null)
        {
            var app = AppCatalog.Get(appId);
            if (app is null) { _log($"App '{appId}' not found."); return; }

            var allAgents = _getAgents()
                .Where(a => a.Registered && a.IsOnline)
                .Select(a => a.AgentId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (allAgents.Count == 0) { _log("No agents online."); return; }

            var now = DateTime.UtcNow;
            var usable = allAgents.Where(id => !_banUntil.TryGetValue(id, out var until) || until <= now).ToList();
            if (usable.Count == 0)
            {
                // Fallback: if all banned, try all (we still want to make forward progress)
                usable = allAgents;
                _log("All agents currently banned; falling back to full set for this run.");
            }

            int partCount = parts ?? Math.Max(usable.Count, 1);
            var corr = Guid.NewGuid().ToString("N");
            var job = new RunningJob
            {
                AppId = appId,
                CorrId = corr,
                Parts = partCount,
                Input = input,
                StartUtc = now
            };
            _jobs[corr] = job;

            // Round-robin assign from usable set
            int k = 0;
            for (int i = 0; i < partCount; i++)
            {
                var agentId = usable[k++ % usable.Count];
                var payload = app.PartitionJob(input, i, partCount);
                var msg = Wire.BuildAppRun(appId, corr, i, partCount, agentId, payload);

                // mark last activity (send moment) so watchdog has a baseline
                _lastActivityUtc[agentId] = DateTime.UtcNow;

                // Timing hook immediately BEFORE the actual send (true send time)
                OnWillSend?.Invoke(agentId, corr, msg);

                _comm.SendToAgent(agentId, msg);

                _ = WatchdogAsync(agentId, corr, i, DateTime.UtcNow);
            }

            _log($"Dispatched {partCount} parts for '{appId}' (corr {corr}) to {usable.Count}/{allAgents.Count} agent(s).");
        }

        /// <summary>
        /// Watchdog: after NoReplyTimeout, if no activity newer than 'sentAt', ban agent for BanCooldown.
        /// </summary>
        private async Task WatchdogAsync(string agentId, string corr, int partIndex, DateTime sentAtUtc)
        {
            try
            {
                await Task.Delay(_noReplyTimeout).ConfigureAwait(false);

                if (_lastActivityUtc.TryGetValue(agentId, out var last) && last > sentAtUtc)
                    return; // activity happened → do nothing

                // No activity → ban
                var until = DateTime.UtcNow + _banCooldown;
                _banUntil[agentId] = until;
                _log($"Agent {agentId} banned until {until:HH:mm:ss} (no reply within {_noReplyTimeout.TotalMilliseconds:n0} ms, corr={corr} part={partIndex}).");
                try { OnAgentBanChanged?.Invoke(agentId, true); } catch { }
            }
            catch { /* swallow */ }
        }

        /// <summary>
        /// Call from your message pump when a line starts with "AppResult:".
        /// Clears ban (if any) and updates last-activity timestamp.
        /// </summary>
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

            // mark activity & clear ban (if any)
            _lastActivityUtc[fromAgentId] = DateTime.UtcNow;
            if (_banUntil.TryRemove(fromAgentId, out _))
            {
                _log($"Agent {fromAgentId} unbanned (received result).");
                try { OnAgentBanChanged?.Invoke(fromAgentId, false); } catch { }
            }

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

            // Push latency up so UI can update per-agent row
            updateLatency?.Invoke(fromAgentId, ms, Avg(job.PartMs.Values), job.PartMs.Count);

            // Metrics hook: close RTT for this part
            OnResultForMetrics?.Invoke(new ResultEnvelope
            {
                AgentId = fromAgentId,
                SenderId = fromAgentId,
                App = appId,
                Seq = i,      // use part index as seq
                CorrId = corr,
                Status = "OK"
            });

            // If complete → merge
            if (job.Results.Count == job.Parts)
            {
                var finalApp = AppCatalog.Get(job.AppId)!;
                var ordered = Enumerable.Range(0, job.Parts).Select(ii => job.Results[ii]).ToList();
                var final = finalApp.MergeResults(ordered);
                var totalMs = (int)(DateTime.UtcNow - job.StartUtc).TotalMilliseconds;

                _log($"Job {job.AppId} ({corr}) complete in {totalMs} ms; parts={job.Parts}.");
                onJobDoneSummary?.Invoke(job.AppId, $"{job.Parts} parts, {totalMs} ms");

                _jobs.TryRemove(corr, out _);
                // TODO: use 'final' (display/save) as needed
            }
            return true;
        }

        /// <summary>
        /// Called by host (UI) on AgentPulse to clear bans early and mark activity.
        /// </summary>
        public void NoteAgentPulse(string agentId)
        {
            _lastActivityUtc[agentId] = DateTime.UtcNow;
            if (_banUntil.TryRemove(agentId, out _))
            {
                _log($"Agent {agentId} unbanned (heartbeat).");
                try { OnAgentBanChanged?.Invoke(agentId, false); } catch { }
            }
        }

        private static double Avg(IEnumerable<int> xs)
        {
            double s = 0; int c = 0;
            foreach (var v in xs) { s += v; c++; }
            return c == 0 ? 0 : s / c;
        }
    }
}







