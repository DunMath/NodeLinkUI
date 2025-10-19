using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace NodeCore.Orchestration
{
    // A unit of work (already split by a planner)
    public sealed class TaskUnit
    {
        public required string App { get; init; }
        public required int Seq { get; init; }
        /// <summary>
        /// User payload. If CorrId/SentUtc are set, ToWire() will embed them into this
        /// field as JSON so legacy agents (that only see a single payload field) keep working.
        /// </summary>
        public required string Payload { get; init; }

        // Optional placement hint
        public string? AffinityAgentId { get; init; }

        // New (non-breaking) metadata for orchestration/telemetry
        public string? CorrId { get; set; }                // correlation id for latency measurement
        public DateTime? SentUtc { get; set; }             // when this unit was dispatched (UTC)
        public int? PayloadBytes { get; set; }             // for diagnostics only
    }

    // What we send on the wire (master -> agent)
    public static class TaskWire
    {
        // Legacy grammar is preserved:
        //   Compute:App|Seq|payload
        //
        // If CorrId or SentUtc are present, we embed them into the single 'payload' field as JSON:
        //   Compute:App|Seq|{"data":"<payload>","corrId":"...","sentUtc":"2025-01-01T12:34:56Z"}
        public static string ToWire(TaskUnit t)
        {
            if (t is null) throw new ArgumentNullException(nameof(t));

            string payloadOut;
            if (!string.IsNullOrEmpty(t.CorrId) || t.SentUtc is not null)
            {
                var sent = (t.SentUtc ?? DateTime.UtcNow);
                t.SentUtc = sent; // ensure populated for caller
                payloadOut = JsonSerializer.Serialize(new
                {
                    data = t.Payload,
                    corrId = t.CorrId ?? Guid.NewGuid().ToString("N"),
                    // ISO 8601 Z so it round-trips well
                    sentUtc = sent.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
                });
            }
            else
            {
                payloadOut = t.Payload;
            }

            if (t.PayloadBytes is null)
                t.PayloadBytes = payloadOut.Length;

            return $"Compute:{t.App}|{t.Seq}|{payloadOut}";
        }
    }

    // What we receive from agents (agent -> master)
    // Accepts either:
    //  - "Result:OK:App|Seq"                                (legacy)
    //  - "ComputeResult:App|Seq:OK"                         (preferred, simple)
    //  - "ComputeResult:App|Seq:OK:{json}"                  (preferred, rich)
    // Where {json} may include: corrId, payload, durationMs, latencyMs, startedUtc, endedUtc, runId, senderId
    public sealed class ResultEnvelope
    {
        public required string AgentId { get; init; }      // network-level sender (always set by parser)
        public required string App { get; init; }
        public required int Seq { get; init; }

        public required string Status { get; init; }        // "OK" / "FAIL" / custom
        public string? Payload { get; init; }               // optional extra result

        // Telemetry / orchestration fields
        public string? CorrId { get; init; }
        public double? DurationMs { get; init; }
        public double? LatencyMs { get; init; }
        public DateTime? StartedUtc { get; init; }
        public DateTime? EndedUtc { get; init; }

        // NEW: identifiers the orchestrator expects
        public string? RunId { get; init; }                 // job/run identifier (set by master/orchestrator)
        public string? SenderId { get; init; }              // logical worker id (defaults to AgentId)

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
                    // Forms:
                    //   ComputeResult:App|Seq:OK
                    //   ComputeResult:App|Seq:OK:{json}
                    var body = raw.Substring("ComputeResult:".Length);
                    var parts = body.Split(':', 3, StringSplitOptions.None); // keep payload json intact
                    if (parts.Length >= 2)
                    {
                        var appSeq = parts[0];
                        var status = parts.Length >= 2 ? parts[1] : "OK";
                        string? maybeJson = parts.Length == 3 ? parts[2] : null;

                        ParseAppSeq(appSeq, out var app, out var seq);

                        string? corrId = null;
                        string? payload = null;
                        double? durationMs = null;
                        double? latencyMs = null;
                        DateTime? startedUtc = null;
                        DateTime? endedUtc = null;

                        // NEW
                        string? runId = null;
                        string? senderId = null;

                        if (!string.IsNullOrWhiteSpace(maybeJson) && LooksLikeJson(maybeJson))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(maybeJson);
                                var root = doc.RootElement;

                                if (root.TryGetProperty("corrId", out var pCorr)) corrId = pCorr.GetString();
                                if (root.TryGetProperty("payload", out var pPay)) payload = pPay.ToString();
                                if (root.TryGetProperty("data", out var pData)) payload ??= pData.ToString();

                                if (root.TryGetProperty("durationMs", out var pDur) && pDur.TryGetDouble(out var d))
                                    durationMs = d;
                                if (root.TryGetProperty("latencyMs", out var pLat) && pLat.TryGetDouble(out var l))
                                    latencyMs = l;

                                if (root.TryGetProperty("startedUtc", out var pStart) &&
                                    DateTime.TryParse(pStart.ToString(), CultureInfo.InvariantCulture,
                                                      DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                                                      out var dtS))
                                    startedUtc = dtS.ToUniversalTime();

                                if (root.TryGetProperty("endedUtc", out var pEnd) &&
                                    DateTime.TryParse(pEnd.ToString(), CultureInfo.InvariantCulture,
                                                      DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                                                      out var dtE))
                                    endedUtc = dtE.ToUniversalTime();

                                // NEW: runId / senderId if provided by agent
                                if (root.TryGetProperty("runId", out var pRun)) runId = pRun.GetString();
                                if (root.TryGetProperty("senderId", out var pSid)) senderId = pSid.GetString();
                                // fallbacks some agents may use
                                if (senderId is null && root.TryGetProperty("from", out var pFrom)) senderId = pFrom.GetString();
                                if (senderId is null && root.TryGetProperty("agentId", out var pAid)) senderId = pAid.GetString();
                            }
                            catch
                            {
                                // Not JSON or malformed – treat as plain payload
                                payload = maybeJson;
                            }
                        }

                        env = new ResultEnvelope
                        {
                            AgentId = agentId,
                            App = app,
                            Seq = seq,
                            Status = string.IsNullOrWhiteSpace(status) ? "OK" : status,
                            Payload = payload,
                            CorrId = corrId,
                            DurationMs = durationMs,
                            LatencyMs = latencyMs,
                            StartedUtc = startedUtc,
                            EndedUtc = endedUtc,
                            RunId = runId,
                            SenderId = senderId ?? agentId // default to network sender
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

                        ParseAppSeq(appSeq, out var app, out var seq);

                        env = new ResultEnvelope
                        {
                            AgentId = agentId,
                            App = app,
                            Seq = seq,
                            Status = status,
                            // legacy format doesn't carry these; keep nulls
                            SenderId = agentId
                        };
                        return true;
                    }
                }
            }
            catch
            {
                // ignore parsing failure
            }
            return false;

            static void ParseAppSeq(string appSeq, out string app, out int seq)
            {
                var asParts = appSeq.Split('|');
                app = asParts[0];
                seq = (asParts.Length > 1 && int.TryParse(asParts[1], out var s)) ? s : 0;
            }

            static bool LooksLikeJson(string s)
                => !string.IsNullOrEmpty(s) && (s.TrimStart().StartsWith("{") || s.TrimStart().StartsWith("["));
        }

        /// <summary>
        /// Helper to build a rich ComputeResult line from a populated ResultEnvelope.
        /// Agents may use this to include timings without changing their existing logic.
        /// </summary>
        public static string BuildResultWire(ResultEnvelope env)
        {
            if (env is null) throw new ArgumentNullException(nameof(env));

            // Keep simple form if no extras present
            var hasExtras = env.CorrId != null || env.Payload != null || env.DurationMs != null ||
                            env.LatencyMs != null || env.StartedUtc != null || env.EndedUtc != null ||
                            env.RunId != null || env.SenderId != null;

            if (!hasExtras)
                return $"ComputeResult:{env.App}|{env.Seq}:{env.Status}";

            var json = new
            {
                corrId = env.CorrId,
                payload = env.Payload,
                durationMs = env.DurationMs,
                latencyMs = env.LatencyMs,
                startedUtc = env.StartedUtc?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                endedUtc = env.EndedUtc?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                runId = env.RunId,
                senderId = env.SenderId
            };

            return $"ComputeResult:{env.App}|{env.Seq}:{env.Status}:{JsonSerializer.Serialize(json)}";
        }
    }
}




