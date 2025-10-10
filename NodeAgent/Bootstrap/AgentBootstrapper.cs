// NodeAgent/Bootstrap/AgentBootstrapper.cs
using System;
using NodeCore.Config;
using NodeCore.Protocol;

namespace NodeAgent.Bootstrap
{
    public static class AgentBootstrapper
    {
        private static NodeLinkConfig _cfg = NodeLinkConfig.Load();
        private static bool _started;

        public static void Start(Func<string> getAgentId,
                                 Func<string> getAgentName,
                                 Func<string> getLocalIp,
                                 Action<string> log)
        {
            if (_started) return;
            _started = true;

            log($"[Agent] CFG Http={_cfg.UseHttpControlPlane} Port={_cfg.ControlPort} Auth={_cfg.AuthToken != null}");

            if (_cfg.UseHttpControlPlane)
            {
                AgentApp.ControlPlane.Start(
                    agentId: getAgentId(),
                    agentName: getAgentName(),
                    agentIp: getLocalIp(),
                    port: _cfg.ControlPort,
                    bearerToken: _cfg.AuthToken
                );
            }
        }
    }
}

