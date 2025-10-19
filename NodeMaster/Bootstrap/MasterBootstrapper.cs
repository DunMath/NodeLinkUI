using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NodeMaster.Bootstrap
{
    /// <summary>
    /// Master-side bootstrapper that owns the Agent registration workflow:
    /// - Issues HTTP POST /nl/register?agentId=...&corr=...
    /// - Retries with backoff
    /// - Validates RegisterAck that must include the corr (back-compat supported)
    /// - Raises UI-friendly events for success/failure
    /// </summary>
    public sealed class MasterBootstrapper : IDisposable
    {
        // ----- Public events the UI (MainWindow) can subscribe to -----
        public event Action<string>? RegistrationSucceeded;                   // agentId
        public event Action<string, string>? RegistrationFailed;              // agentId, reason
        public event Action<string, int, int, string>? RegistrationAttempt;   // agentId, attempt, max, corr

        // ----- Configuration (tweakable) -----
        private readonly int _maxAttempts = 4;
        private readonly TimeSpan _httpTimeout = TimeSpan.FromSeconds(4);
        private readonly TimeSpan _ackWait = TimeSpan.FromSeconds(3);
        private readonly TimeSpan _betweenAttempts = TimeSpan.FromMilliseconds(500);

        // ----- Plumbing -----
        private readonly HttpClient _http;
        private readonly Action<string> _log;

        // Prevent parallel registration attempts per agent
        private sealed class Inflight
        {
            public string CurrentCorr = "";
            public int Attempt = 0;
        }
        private readonly ConcurrentDictionary<string, Inflight> _inflightByAgent = new(); // key = agentId

        // Match ACKs to attempts
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _ackWaiters = new(); // key = corr
        private readonly ConcurrentDictionary<string, string> _corrToAgent = new(); // key = corr -> agentId

        private volatile bool _disposed;

        public MasterBootstrapper(Action<string> log)
        {
            _log = log ?? (_ => { });

            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30)
            };
            _http = new HttpClient(handler);
            _http.Timeout = _httpTimeout;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var kvp in _ackWaiters)
                kvp.Value.TrySetCanceled();

            _http.Dispose();
        }

        /// <summary>
        /// UI calls this to start a registration workflow for a given agent.
        /// </summary>
        public Task RegisterAgent(string agentId, string ip)
            => RegisterAgentInternalAsync(agentId, ip);

        /// <summary>
        /// MainWindow should forward raw agent messages here so we can validate ACKs.
        /// Expected ACK: "RegisterAck:AgentId:<corr>" (preferred) or legacy "RegisterAck:AgentId:timestamp".
        /// </summary>
        public void OnAgentMessage(string agentId, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            if (!message.StartsWith("RegisterAck:", StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                // Accept both: RegisterAck:AgentId:<corr>  and  RegisterAck:AgentId:<timestamp>
                // We only "complete" a waiter if a corr is included and matches a pending entry.
                var parts = message.Split(':', StringSplitOptions.RemoveEmptyEntries);
                // parts[0] = "RegisterAck"
                var ackAgentId = parts.Length > 1 ? parts[1].Trim() : agentId;

                string? corr = null;
                if (parts.Length >= 3)
                {
                    // parts[2] might be a corr (GUID) in the new format, or a timestamp (legacy).
                    var token = parts[2].Trim();
                    if (Guid.TryParse(token, out _))
                        corr = token;
                    else if (parts.Length >= 4 && Guid.TryParse(parts[3].Trim(), out _))
                        corr = parts[3].Trim();
                }

                if (!string.IsNullOrWhiteSpace(corr))
                {
                    // New format: match corr precisely
                    if (_ackWaiters.TryGetValue(corr, out var tcs))
                    {
                        // Avoid double-complete
                        if (tcs.TrySetResult(true))
                        {
                            _log($"RegisterAck accepted from {ackAgentId} (corr match).");
                            RegistrationSucceeded?.Invoke(ackAgentId);
                        }
                    }
                    else
                    {
                        // Corr we didn't issue (or already timed out) – ignore without UI noise.
                        _log("CorrelationId mismatch (stale or unexpected ACK).");
                    }
                }
                else
                {
                    // Legacy ACK (no corr). Best-effort: accept if we have an inflight slot for this agent.
                    if (_inflightByAgent.ContainsKey(ackAgentId))
                    {
                        _log($"RegisterAck accepted from {ackAgentId} (legacy format).");
                        RegistrationSucceeded?.Invoke(ackAgentId);
                    }
                    else
                    {
                        _log("RegisterAck ignored (no in-flight entry and no corr).");
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"RegisterAck parse/handle error: {ex.Message}");
            }
        }

        // ------------------------- internals -------------------------

        private async Task RegisterAgentInternalAsync(string agentId, string ip)
        {
            if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(ip))
            {
                RegistrationFailed?.Invoke(agentId, "Invalid agentId or IP.");
                return;
            }

            // in-flight guard
            if (!_inflightByAgent.TryAdd(agentId, new Inflight()))
            {
                _log($"Registration already in-flight for {agentId} – skipped.");
                return;
            }

            try
            {
                var inflight = _inflightByAgent[agentId];

                for (int attempt = 1; attempt <= _maxAttempts; attempt++)
                {
                    inflight.Attempt = attempt;

                    var corr = Guid.NewGuid().ToString();
                    inflight.CurrentCorr = corr;
                    _corrToAgent[corr] = agentId;

                    RegistrationAttempt?.Invoke(agentId, attempt, _maxAttempts, corr);
                    _log($"RegisterRequest -> {ip}:50555 (attempt {attempt}/{_maxAttempts}, corr {corr})");

                    var url = $"http://{ip}:50555/nl/register?agentId={WebUtility.UrlEncode(agentId)}&corr={WebUtility.UrlEncode(corr)}";

                    bool httpOk = false;
                    try
                    {
                        using var cts = new CancellationTokenSource(_httpTimeout);
                        using var resp = await _http.PostAsync(url, new ByteArrayContent(Array.Empty<byte>()), cts.Token);
                        if ((int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300)
                        {
                            _log("Register HTTP OK (awaiting ACK).");
                            httpOk = true;
                        }
                        else
                        {
                            _log($"Register HTTP {(int)resp.StatusCode} ERR");
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        _log("Register HTTP timeout.");
                    }
                    catch (Exception ex)
                    {
                        _log($"Register network error: {ex.Message}");
                    }

                    if (httpOk)
                    {
                        // Wait briefly for a matching ACK
                        var ok = await WaitForAckAsync(agentId, corr, _ackWait);
                        if (ok)
                        {
                            // Success event is raised in OnAgentMessage when ACK arrives.
                            return;
                        }
                        else
                        {
                            _log("No matching ACK within window.");
                        }
                    }

                    await Task.Delay(_betweenAttempts);
                }

                RegistrationFailed?.Invoke(agentId, "All attempts exhausted without a matching ACK.");
                _log("Register failed after all retries.");
            }
            finally
            {
                _inflightByAgent.TryRemove(agentId, out _);
            }
        }

        private async Task<bool> WaitForAckAsync(string agentId, string corr, TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ackWaiters[corr] = tcs;

            using var cts = new CancellationTokenSource(timeout);
            using var reg = cts.Token.Register(() => tcs.TrySetResult(false));

            var result = await tcs.Task;

            _ackWaiters.TryRemove(corr, out _);
            _corrToAgent.TryRemove(corr, out _);

            return result;
        }
    }
}





