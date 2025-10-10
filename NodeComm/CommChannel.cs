// NodeComm/CommChannel.cs
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NodeComm
{
    public sealed class CommChannel : IDisposable
    {
        public event Action<string, string>? OnMessageReceived;

        // Config
        private const int DefaultPort = 5000;
        private readonly int _port;

        // Sockets & loops
        private UdpClient? _udp;
        private CancellationTokenSource? _cts;

        // Role & addressing
        private bool _isMaster;
        private bool _isAgent;
        private string _localId = "";
        private string? _localAgentId; // when running as agent, we know our id
        private IPEndPoint? _masterEndpoint;

        // Agent endpoint map (master uses this to unicast to agents)
        private readonly ConcurrentDictionary<string, IPEndPoint> _agentEndpoints =
            new(StringComparer.OrdinalIgnoreCase);

        // Reverse map (optional, helps guessing agent id from remote EP)
        private readonly ConcurrentDictionary<string, string> _endpointToAgentId =
            new(StringComparer.OrdinalIgnoreCase);

        public CommChannel(int port = DefaultPort)
        {
            _port = port;
        }

        // ---------------- Initialization ----------------

        public void InitializeAsMaster(string masterId)
        {
            _isMaster = true;
            _localId = masterId ?? "Master";
            StartUdp(bindAny: true);
        }

        public void InitializeAsAgent(string agentId, string masterIp)
        {
            _isAgent = true;
            _localId = agentId ?? "Agent";
            _localAgentId = _localId;
            SetMasterIp(masterIp);
            StartUdp(bindAny: true);
        }

        private void StartUdp(bool bindAny)
        {
            StopUdp();

            _cts = new CancellationTokenSource();

            _udp = new UdpClient(new IPEndPoint(bindAny ? IPAddress.Any : IPAddress.Loopback, _port));
            _udp.EnableBroadcast = true;

            _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && _udp != null)
                {
                    UdpReceiveResult r;
                    try
                    {
                        r = await _udp.ReceiveAsync(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch { continue; }

                    string msg;
                    try
                    {
                        msg = Encoding.UTF8.GetString(r.Buffer);
                    }
                    catch
                    {
                        continue;
                    }

                    // Decide "agentId" for the event we raise:
                    //  - If we're an agent: any incoming msg is for _localAgentId
                    //  - If we're master: try to guess agent id from message or endpoint map
                    string agentIdForEvent = _isAgent
                        ? (_localAgentId ?? "Agent-Unknown")
                        : GuessAgentIdForMaster(msg, r.RemoteEndPoint);

                    // As Master, learn/refresh endpoint mapping when we can associate an id.
                    if (_isMaster && !string.IsNullOrWhiteSpace(agentIdForEvent))
                    {
                        LearnEndpoint(agentIdForEvent, r.RemoteEndPoint);
                    }

                    try { OnMessageReceived?.Invoke(agentIdForEvent, msg); }
                    catch { /* swallow to keep loop alive */ }
                }
            }
            catch { /* keep quiet */ }
        }

        // ---------------- Endpoint learning (Master) ----------------

        private void LearnEndpoint(string agentId, IPEndPoint remote)
        {
            if (string.IsNullOrWhiteSpace(agentId) || remote == null) return;
            _agentEndpoints[agentId] = remote;
            _endpointToAgentId[remote.ToString()] = agentId;
        }

        private string GuessAgentIdForMaster(string message, IPEndPoint remote)
        {
            // Try fast reverse map first
            var key = remote.ToString();
            if (_endpointToAgentId.TryGetValue(key, out var mapped))
                return mapped;

            // Try parse by well-known message shapes (cheap string ops)

            // RegisterAck:{agentId}:{json}
            if (message.StartsWith("RegisterAck:", StringComparison.OrdinalIgnoreCase))
            {
                var first = message.IndexOf(':');
                var second = first >= 0 ? message.IndexOf(':', first + 1) : -1;
                if (first >= 0 && second > first)
                {
                    var id = message.Substring(first + 1, second - first - 1);
                    if (!string.IsNullOrWhiteSpace(id)) return id;
                }
            }

            // RegisterHello:{agentId}:{json}
            if (message.StartsWith("RegisterHello:", StringComparison.OrdinalIgnoreCase))
            {
                var first = message.IndexOf(':');
                var second = first >= 0 ? message.IndexOf(':', first + 1) : -1;
                if (first >= 0 && second > first)
                {
                    var id = message.Substring(first + 1, second - first - 1);
                    if (!string.IsNullOrWhiteSpace(id)) return id;
                }
            }

            // PingReply:{agentId}
            if (message.StartsWith("PingReply:", StringComparison.OrdinalIgnoreCase))
            {
                var id = message.Substring("PingReply:".Length).Trim();
                if (!string.IsNullOrWhiteSpace(id)) return id;
            }

            // AgentConfig:{json}  -> find "AgentId":"..."
            if (message.StartsWith("AgentConfig:", StringComparison.OrdinalIgnoreCase))
            {
                var id = ExtractAgentIdFromJsonTail(message, "AgentConfig:");
                if (!string.IsNullOrWhiteSpace(id)) return id;
            }

            // AgentPulse:{json}   -> find "AgentId":"..."
            if (message.StartsWith("AgentPulse:", StringComparison.OrdinalIgnoreCase))
            {
                var id = ExtractAgentIdFromJsonTail(message, "AgentPulse:");
                if (!string.IsNullOrWhiteSpace(id)) return id;
            }

            // AgentStatus:{json}  (legacy)  -> find "AgentId":"..."
            if (message.StartsWith("AgentStatus:", StringComparison.OrdinalIgnoreCase))
            {
                var id = ExtractAgentIdFromJsonTail(message, "AgentStatus:");
                if (!string.IsNullOrWhiteSpace(id)) return id;
            }

            // Generic JSON fallback: try to find "AgentId":"..." anywhere
            var generic = ExtractAgentIdFromJson(message);
            if (!string.IsNullOrWhiteSpace(generic)) return generic;

            // Fallback to remote endpoint
            return remote.Address.ToString();
        }

        private static string ExtractAgentIdFromJsonTail(string message, string prefix)
        {
            try
            {
                int idx = message.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return string.Empty;
                string json = message.Substring(idx + prefix.Length);
                return ExtractAgentIdFromJson(json);
            }
            catch { return string.Empty; }
        }

        private static string ExtractAgentIdFromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return string.Empty;

            // Extremely lightweight scan: look for "AgentId":"..."
            int keyIdx = json.IndexOf("\"AgentId\"", StringComparison.OrdinalIgnoreCase);
            if (keyIdx < 0) keyIdx = json.IndexOf("'AgentId'", StringComparison.OrdinalIgnoreCase);
            if (keyIdx < 0) return string.Empty;

            // Find the next quote after the colon
            int colon = json.IndexOf(':', keyIdx);
            if (colon < 0) return string.Empty;

            // Skip whitespace
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;

            if (i >= json.Length) return string.Empty;

            char quote = json[i];
            if (quote != '"' && quote != '\'') return string.Empty;

            int start = i + 1;
            int end = json.IndexOf(quote, start);
            if (end > start) return json.Substring(start, end - start);

            return string.Empty;
        }

        // ---------------- Addressing helpers ----------------

        public void SetMasterIp(string ip)
        {
            if (IPAddress.TryParse(ip, out var addr))
                _masterEndpoint = new IPEndPoint(addr, _port);
        }

        public void UpdateAgentEndpoint(string agentId, string ip)
        {
            if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(ip)) return;
            if (!IPAddress.TryParse(ip, out var addr)) return;

            var ep = new IPEndPoint(addr, _port);
            _agentEndpoints[agentId] = ep;
            _endpointToAgentId[ep.ToString()] = agentId;
        }

        // ---------------- Send APIs ----------------

        public bool SendToMaster(string message)
        {
            var ep = _masterEndpoint;
            if (ep == null || _udp == null) return false;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                _udp.Send(bytes, bytes.Length, ep);
                return true;
            }
            catch { return false; }
        }

        public bool SendToAgent(string agentId, string message)
        {
            if (_udp == null) return false;
            if (!_agentEndpoints.TryGetValue(agentId, out var ep)) return false;

            try
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                _udp.Send(bytes, bytes.Length, ep);
                return true;
            }
            catch { return false; }
        }

        public bool Broadcast(string message)
        {
            if (_udp == null) return false;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                // broadcast on the same port
                var ep = new IPEndPoint(IPAddress.Broadcast, _port);
                _udp.Send(bytes, bytes.Length, ep);
                return true;
            }
            catch { return false; }
        }
        // Broadcast a Ping that carries the Master's identity so Agents can unicast back.
        public bool BroadcastPing(string masterId, string masterIp, int sequence)
        {
            if (_udp == null) return false;
            try
            {
                // Format: Ping:<MasterId>:<MasterIp>:<Seq>
                var payload = $"Ping:{masterId}:{masterIp}:{sequence}";
                var bytes = Encoding.UTF8.GetBytes(payload);
                var ep = new IPEndPoint(IPAddress.Broadcast, _port);
                _udp.Send(bytes, bytes.Length, ep);
                return true;
            }
            catch { return false; }
        }
        // Convenience: WhoIsAlive broadcast
        public bool BroadcastWhoIsAlive() => Broadcast("WhoIsAlive");

        // ---------------- Back-compat shims ----------------

        /// <summary>
        /// Older code calls RegisterMaster(handler) and expects a single-arg message handler.
        /// We forward all incoming messages to that handler.
        /// </summary>
        public void RegisterMaster(Action<string> handler)
        {
            OnMessageReceived += (agentId, message) =>
            {
                try { handler(message); } catch { /* keep comms alive */ }
            };
        }
        // Unicast nudge to a known agent endpoint (fallback when broadcast is flaky).
        public bool SendWhoIsAlive(string agentId)
        {
            if (_udp == null) return false;
            if (!_agentEndpoints.TryGetValue(agentId, out var ep)) return false;

            try
            {
                var bytes = Encoding.UTF8.GetBytes("WhoIsAlive");
                _udp.Send(bytes, bytes.Length, ep);
                return true;
            }
            catch { return false; }
        }
        /// <summary>
        /// Older code calls Register(agentId, handler) to receive messages for a specific agent.
        /// We filter OnMessageReceived by agentId and forward only those messages.
        /// </summary>
        public void Register(string agentId, Action<string> handler)
        {
            if (string.IsNullOrWhiteSpace(agentId)) return;

            OnMessageReceived += (targetId, message) =>
            {
                if (string.Equals(targetId, agentId, StringComparison.OrdinalIgnoreCase))
                {
                    try { handler(message); } catch { /* keep comms alive */ }
                }
            };
        }

        // ---------------- Lifecycle ----------------

        private void StopUdp()
        {
            try { _cts?.Cancel(); } catch { }
            try { _udp?.Close(); } catch { }
            try { _udp?.Dispose(); } catch { }
            _udp = null;
            _cts = null;
        }

        public void Dispose()
        {
            StopUdp();
        }
    }
}






