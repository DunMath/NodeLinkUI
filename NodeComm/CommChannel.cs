using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NodeComm
{
    /// <summary>
    /// Lightweight UDP comms for Master & Agents.
    /// - Master listens on MasterPort; Agents listen on AgentPort.
    /// - Agent needs SetMasterIp or InitializeAsAgent(masterIp) to talk to Master.
    /// - Master needs UpdateAgentEndpoint(agentId, ip) to talk to Agents.
    /// </summary>
    public sealed class CommChannel : IDisposable
    {
        public const int MasterPort = 5055;
        public const int AgentPort = 5056;

        /// <summary>Raised when a message addressed to this node is received: (fromId, payload).</summary>
        public event Action<string, string>? OnMessageReceived;

        // --- State ---
        private readonly ConcurrentDictionary<string, IPEndPoint> _agentEndpoints =
            new(StringComparer.OrdinalIgnoreCase);

        private IPEndPoint? _masterEndpoint;
        private UdpClient? _udp;
        private CancellationTokenSource? _cts;
        private Task? _rxTask;

        private bool _isMaster;
        private string _localId = "Unknown";
        private readonly JsonSerializerOptions _jso = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private readonly object _sync = new();

        // =============== Public API ===============

        /// <summary>Bind as Master and start the receive loop.</summary>
        public void InitializeAsMaster(string localId = "Master")
        {
            lock (_sync)
            {
                Stop();
                _isMaster = true;
                _localId = string.IsNullOrWhiteSpace(localId) ? "Master" : localId;
                _udp = BindUdp(MasterPort);
                StartReceiveLoop();
            }
        }

        /// <summary>Bind as Agent and start the receive loop.</summary>
        public void InitializeAsAgent(string localId, string masterIp)
        {
            if (string.IsNullOrWhiteSpace(masterIp))
                throw new ArgumentException("Master IP required for agent.", nameof(masterIp));

            lock (_sync)
            {
                Stop();
                _isMaster = false;
                _localId = string.IsNullOrWhiteSpace(localId) ? $"Agent-{Environment.MachineName}" : localId;
                _masterEndpoint = new IPEndPoint(IPAddress.Parse(masterIp), MasterPort);
                _udp = BindUdp(AgentPort);
                StartReceiveLoop();
            }
        }

        /// <summary>Update the known Master IP (Agent side).</summary>
        public void SetMasterIp(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return;
            _masterEndpoint = new IPEndPoint(IPAddress.Parse(ip), MasterPort);
        }

        /// <summary>Update a known Agent IP (Master side).</summary>
        public void UpdateAgentEndpoint(string agentId, string ip)
        {
            if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(ip)) return;
            _agentEndpoints[agentId] = new IPEndPoint(IPAddress.Parse(ip), AgentPort);
        }

        /// <summary>Send to Master (Agent side).</summary>
        public bool SendToMaster(string payload)
        {
            if (_masterEndpoint == null) return false;
            return SendEnvelope(_masterEndpoint, Envelope.For(_localId, "Master", payload));
        }

        /// <summary>Send to a specific Agent (Master side).</summary>
        public bool SendToAgent(string agentId, string payload)
        {
            if (_agentEndpoints.TryGetValue(agentId, out var ep))
                return SendEnvelope(ep, Envelope.For(_localId, agentId, payload));
            return false;
        }

        /// <summary>Broadcast to all Agents on the LAN (Master side).</summary>
        public bool Broadcast(string payload)
        {
            try
            {
                using var bc = new UdpClient { EnableBroadcast = true };
                var ep = new IPEndPoint(IPAddress.Broadcast, AgentPort);
                var env = Envelope.For(_localId, "*", payload);
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(env, _jso));
                bc.Send(bytes, bytes.Length, ep);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Legacy in-proc registration helpers (optional; keep for backward compatibility).
        public void Register(string agentId, Action<string> receiver)
        {
            // Bridge: call receiver when a message arrives FROM that agentId
            OnMessageReceived += (fromId, message) =>
            {
                if (string.Equals(fromId, agentId, StringComparison.OrdinalIgnoreCase))
                    receiver?.Invoke(message);
            };
        }

        public void RegisterMaster(Action<string> receiver)
        {
            // Bridge: call receiver when a message is addressed to Master
            OnMessageReceived += (fromId, message) =>
            {
                if (_isMaster) receiver?.Invoke(message);
            };
        }

        public void Dispose() => Stop();

        // =============== Internals ===============

        private static UdpClient BindUdp(int port)
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            return udp;
        }

        private void StartReceiveLoop()
        {
            if (_udp == null) return;
            _cts = new CancellationTokenSource();
            _rxTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }

        private void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _udp?.Close(); } catch { }
            try { _udp?.Dispose(); } catch { }
            _udp = null;
            _cts = null;
            _rxTask = null;
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            if (_udp == null) return;

            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult res;
                try { res = await _udp.ReceiveAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch { continue; }

                try
                {
                    var json = Encoding.UTF8.GetString(res.Buffer);
                    var env = JsonSerializer.Deserialize<Envelope>(json, _jso);
                    if (env == null) continue;

                    // Accept if to me, to Master (and I'm Master), or wildcard
                    bool toMe =
                        env.ToId == "*" ||
                        string.Equals(env.ToId, _localId, StringComparison.OrdinalIgnoreCase) ||
                        (_isMaster && string.Equals(env.ToId, "Master", StringComparison.OrdinalIgnoreCase));

                    if (toMe)
                        OnMessageReceived?.Invoke(env.FromId ?? "Unknown", env.Payload ?? "");
                }
                catch
                {
                    // ignore malformed
                }
            }
        }

        private bool SendEnvelope(IPEndPoint ep, Envelope env)
        {
            try
            {
                if (_udp == null)
                {
                    // fire-and-forget socket for one-shot send if not bound yet
                    using var oneShot = new UdpClient(AddressFamily.InterNetwork);
                    var js = JsonSerializer.Serialize(env, _jso);
                    var buf = Encoding.UTF8.GetBytes(js);
                    oneShot.Send(buf, buf.Length, ep);
                    return true;
                }

                var json = JsonSerializer.Serialize(env, _jso);
                var bytes = Encoding.UTF8.GetBytes(json);
                _udp.Send(bytes, bytes.Length, ep);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private sealed class Envelope
        {
            public string Version { get; set; } = "1";
            public string? Type { get; set; } = "msg";
            public string? FromId { get; set; }
            public string? ToId { get; set; }
            public string? Payload { get; set; }

            public static Envelope For(string from, string to, string payload) =>
                new Envelope { FromId = from, ToId = to, Payload = payload };
        }
    }
}




