// NodeAgent/ControlPlane.cs (HttpListener-based: no ASP.NET deps)
using System;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NodeCore.Protocol;

namespace AgentApp
{
    public static class ControlPlane
    {
        private static HttpListener? _listener;
        private static CancellationTokenSource? _cts;

        public static void Start(
            string agentId,
            string agentName,
            string agentIp,
            int port = Proto.DefaultControlPort,
            string? bearerToken = null)
        {
            if (_listener != null) return;

            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            var prefix = $"http://+:{port}/";
            _listener.Prefixes.Add(prefix);
            try
            {
                _listener.Start();
                Console.WriteLine($"[Agent] Control plane listening on {prefix}");
            }
            catch (HttpListenerException ex)
            {
                Console.WriteLine($"[Agent] Control plane could not start: {ex.Message}");
                _listener = null;
                return;
            }

            _ = Task.Run(() => AcceptLoop(agentId, agentName, agentIp, bearerToken, _cts.Token));
        }

        public static void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _listener = null;
        }

        private static async Task AcceptLoop(string agentId, string agentName, string agentIp, string? token, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                HttpListenerContext? ctx = null;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch when (ct.IsCancellationRequested) { break; }
                catch { continue; }

                _ = Task.Run(() => Handle(ctx, agentId, agentName, agentIp, token));
            }
        }

        private static async Task Handle(HttpListenerContext ctx, string agentId, string agentName, string agentIp, string? token)
        {
            var req = ctx.Request;
            var resp = ctx.Response;

            // /nl/health (GET)
            if (req.HttpMethod == "GET" && req.Url?.AbsolutePath.Equals("/nl/health", StringComparison.OrdinalIgnoreCase) == true)
            {
                await WriteString(resp, 200, "OK");
                return;
            }

            // /nl/register (POST + optional Bearer)
            if (req.HttpMethod == "POST" && req.Url?.AbsolutePath.Equals("/nl/register", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (!AuthOk(req, token)) { resp.StatusCode = 401; resp.Close(); return; }

                try
                {
                    using var reader = new System.IO.StreamReader(req.InputStream, req.ContentEncoding);
                    var body = await reader.ReadToEndAsync();
                    var reg = JsonSerializer.Deserialize<RegisterRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (reg == null) { await WriteString(resp, 400, "Malformed"); return; }

                    RegisterAck ack;
                    if (!string.Equals(reg.Version, Proto.Version, StringComparison.Ordinal))
                    {
                        ack = new RegisterAck(
                            Version: Proto.Version,
                            AgentId: agentId,
                            AgentName: agentName,
                            AgentIp: agentIp,
                            CorrelationId: reg.CorrelationId,
                            Accepted: false,
                            Reason: $"Version mismatch. Agent={Proto.Version} Master={reg.Version}"
                        );
                    }
                    else
                    {
                        ack = new RegisterAck(
                            Version: Proto.Version,
                            AgentId: agentId,
                            AgentName: agentName,
                            AgentIp: agentIp,
                            CorrelationId: reg.CorrelationId,
                            Accepted: true
                        );
                    }

                    var json = JsonSerializer.Serialize(ack);
                    await WriteJson(resp, 200, json);
                    return;
                }
                catch (Exception ex)
                {
                    await WriteString(resp, 500, $"Error: {ex.Message}");
                    return;
                }
            }

            // Not found
            resp.StatusCode = 404;
            resp.Close();
        }

        // Helpers
        private static bool AuthOk(HttpListenerRequest req, string? token)
        {
            if (string.IsNullOrEmpty(token)) return true;
            var auth = req.Headers["Authorization"];
            return string.Equals(auth, $"Bearer {token}", StringComparison.Ordinal);
        }

        private static async Task WriteString(HttpListenerResponse resp, int status, string text)
        {
            var data = Encoding.UTF8.GetBytes(text);
            resp.StatusCode = status;
            resp.ContentType = "text/plain; charset=utf-8";
            resp.ContentLength64 = data.LongLength;
            await resp.OutputStream.WriteAsync(data, 0, data.Length);
            resp.Close();
        }

        private static async Task WriteJson(HttpListenerResponse resp, int status, string json)
        {
            var data = Encoding.UTF8.GetBytes(json);
            resp.StatusCode = status;
            resp.ContentType = "application/json; charset=utf-8";
            resp.ContentLength64 = data.LongLength;
            await resp.OutputStream.WriteAsync(data, 0, data.Length);
            resp.Close();
        }
    }
}


