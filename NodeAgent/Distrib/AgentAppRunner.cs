// NodeAgent/Distrib/AgentAppRunner.cs
using System;
using NodeCore.Distrib;
using NodeComm;

namespace NodeAgent.Distrib
{
    public static class AgentAppRunner
    {
        // Call this from your Agent message pump BEFORE legacy handlers.
        // Returns true if the message was an AppRun and was handled.
        public static bool TryHandle(string incoming, CommChannel comm, Func<string> getAgentId)
        {
            if (!incoming.StartsWith("AppRun:", StringComparison.OrdinalIgnoreCase)) return false;

            // AppRun:{AppId}|{CorrId}|{i}/{n}|{AgentId}\n{base64}
            var nl = incoming.IndexOf('\n');
            var header = nl > 0 ? incoming[..nl] : incoming;
            var payloadB64 = nl > 0 ? incoming[(nl + 1)..] : "";

            var h = header.Split(':', 2)[1].Split('|');
            string appId = h[0];
            string corr = h[1];
            var idxPair = h[2].Split('/');
            int i = int.Parse(idxPair[0]);
            int n = int.Parse(idxPair[1]);

            var app = AppCatalog.Get(appId);
            if (app is null)
            {
                comm.SendToMaster(Wire.BuildAppResultFail(appId, corr, i, n, 0, "App not found"));
                return true;
            }

            var payload = Convert.FromBase64String(payloadB64);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = app.ExecutePart(payload);
                sw.Stop();
                comm.SendToMaster(Wire.BuildAppResultOk(appId, corr, i, n, (int)sw.ElapsedMilliseconds, result));
            }
            catch (Exception ex)
            {
                sw.Stop();
                comm.SendToMaster(Wire.BuildAppResultFail(appId, corr, i, n, (int)sw.ElapsedMilliseconds, ex.Message));
            }
            return true;
        }
    }
}

