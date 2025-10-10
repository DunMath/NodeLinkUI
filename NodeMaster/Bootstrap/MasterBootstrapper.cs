// NodeMaster/Bootstrap/MasterBootstrapper.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using MasterApp;
using NodeCore;
using NodeCore.Config;
using NodeCore.Protocol;

namespace NodeMaster.Bootstrap
{
    public static class MasterBootstrapper
    {
        private static NodeLinkConfig _cfg = NodeLinkConfig.Load();
        private static readonly RegistrationClient _regClient = new();
        private static readonly HttpClient _http = new();
        private static System.Timers.Timer? _healthTimer;

        // Call once at startup
        public static void Initialize(
            Action<string> log,
            Func<IEnumerable<AgentStatus>> getAgents,
            Action<AgentStatus> updateAgent,
            Func<string> getMasterId,
            Func<string> getMasterIp)
        {
            _log = log ?? (_ => { });
            _getAgents = getAgents ?? (() => Enumerable.Empty<AgentStatus>());
            _updateAgent = updateAgent ?? (_ => { });
            _getMasterId = getMasterId ?? (() => "Master");
            _getMasterIp = getMasterIp ?? (() => "127.0.0.1");

            _log($"[Master] CFG Http={_cfg.UseHttpControlPlane} UdpReg={_cfg.UseLegacyUdpRegistration} HealthSecs={_cfg.HealthPollSeconds}");

            if (_healthTimer == null && _cfg.HealthPollSeconds > 0)
            {
                _healthTimer = new System.Timers.Timer(Math.Max(5, _cfg.HealthPollSeconds) * 1000);
                _healthTimer.Elapsed += async (_, __) =>
                {
                    try
                    {
                        foreach (var a in _getAgents()
                                 .Where(x => !string.Equals(x.AgentId, "Master", StringComparison.OrdinalIgnoreCase))
                                 .Where(x => !string.IsNullOrWhiteSpace(x.IpAddress)))
                        {
                            var ip = a.IpAddress!;
                            int port = _cfg.ControlPort; // using config default
                            bool ok = await HealthCheckAsync(ip, port);
                            a.IsOnline = ok;
                            a.LastHeartbeat = DateTime.Now;   // <-- swap from LastSeen
                            _updateAgent(a);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"[Health] loop error: {ex.Message}");
                    }
                };
                _healthTimer.AutoReset = true;
                _healthTimer.Start();
            }
        }

        // Call from discovery handler (typed)
        public static void OnAgentDiscovered(AgentStatus agent)
        {
            if (!_cfg.UseHttpControlPlane) return;

            int port = _cfg.ControlPort;
            _ = _regClient.TryRegisterAsync(
                agentIp: agent.IpAddress ?? "",
                agentPort: port,
                masterId: _getMasterId(),
                masterIp: _getMasterIp(),
                bearerToken: _cfg.AuthToken,
                log: _log);
        }

        private static async Task<bool> HealthCheckAsync(string ip, int port)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{ip}:{port}/nl/health");
                if (!string.IsNullOrEmpty(_cfg.AuthToken))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.AuthToken);
                using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                return res.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        private static Action<string> _log = _ => { };
        private static Func<IEnumerable<AgentStatus>> _getAgents = () => Enumerable.Empty<AgentStatus>();
        private static Action<AgentStatus> _updateAgent = _ => { };
        private static Func<string> _getMasterId = () => "Master";
        private static Func<string> _getMasterIp = () => "127.0.0.1";
    }
}




