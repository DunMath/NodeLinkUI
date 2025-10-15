using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NodeCore.Orchestration
{
    // A unit of work (already split by a planner)
    public sealed class TaskUnit
    {
        public required string App { get; init; }
        public required int Seq { get; init; }
        public required string Payload { get; init; }
        public string? AffinityAgentId { get; init; } // optional sticky placement
    }

    // What we send on the wire (master -> agent)
    public static class TaskWire
    {
        // Compute:App|Seq|payload
        public static string ToWire(TaskUnit t) => $"Compute:{t.App}|{t.Seq}|{t.Payload}";
    }

    // What we receive from agents (agent -> master)
    // Accepts either:
    //  - "Result:OK:App|Seq"            (legacy)
    //  - "ComputeResult:App|Seq:OK"     (preferred)
    public sealed class ResultEnvelope
    {
        public required string AgentId { get; init; }
        public required string App { get; init; }
        public required int Seq { get; init; }
        public required string Status { get; init; } // "OK" / "FAIL" / custom
        public string? Payload { get; init; }        // optional extra result
        public TimeSpan? Duration { get; init; }     // optional timing
        public double? LatencyMs { get; init; }      // optional latency calc

        public bool IsOk => string.Equals(Status, "OK", StringComparison.OrdinalIgnoreCase);
    }

    public static class ResultWire
    {
        public static bool TryParseFromWire(string raw, string agentId, out ResultEnvelope env)
        {
            env = null!;
            try
            {
                if (raw.StartsWith("ComputeResult:", StringComparison.OrdinalIgnoreCase))
                {
                    // ComputeResult:App|Seq[:Status[:payloadJson]]
                    var body = raw.Substring("ComputeResult:".Length);
                    var parts = body.Split(':');
                    if (parts.Length >= 2)
                    {
                        var appSeq = parts[0];
                        var status = (parts.Length >= 2) ? parts[1] : "OK";
                        string? payload = parts.Length >= 3 ? string.Join(':', parts, 2, parts.Length - 2) : null;

                        var asParts = appSeq.Split('|');
                        var app = asParts[0];
                        var seq = asParts.Length > 1 && int.TryParse(asParts[1], out var s) ? s : 0;

                        env = new ResultEnvelope
                        {
                            AgentId = agentId,
                            App = app,
                            Seq = seq,
                            Status = status,
                            Payload = payload
                        };
                        return true;
                    }
                }
                else if (raw.StartsWith("Result:", StringComparison.OrdinalIgnoreCase))
                {
                    // Result:OK:App|Seq  (legacy)
                    var parts = raw.Split(':');
                    if (parts.Length >= 3)
                    {
                        var status = parts[1];
                        var appSeq = parts[2];
                        var asParts = appSeq.Split('|');
                        var app = asParts[0];
                        var seq = asParts.Length > 1 && int.TryParse(asParts[1], out var s) ? s : 0;

                        env = new ResultEnvelope
                        {
                            AgentId = agentId,
                            App = app,
                            Seq = seq,
                            Status = status
                        };
                        return true;
                    }
                }
            }
            catch { /* ignore */ }
            return false;
        }
    }
}

