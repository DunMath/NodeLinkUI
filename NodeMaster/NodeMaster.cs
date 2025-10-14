// NodeMaster/NodeMaster.cs  — SIMPLE snapshot + dispatch (no ref returns, no AgentCount)
using System;
using System.Collections.Generic;
using System.Linq;
using NodeComm;
using NodeCore;

namespace NodeMaster
{
    public class NodeMaster
    {
        private readonly CommChannel comm;

        // Optional internal snapshot (UI can ignore this if it keeps its own list)
        private readonly Dictionary<string, AgentStatus> agents =
            new(StringComparer.OrdinalIgnoreCase);

        public NodeMaster(CommChannel comm)
        {
            this.comm = comm ?? throw new ArgumentNullException(nameof(comm));
        }

        /// <summary>Ask running agents to announce themselves.</summary>
        public void NudgeAgents()
        {
            try { comm.Broadcast("WhoIsAlive"); }
            catch { /* ignore */ }
        }

        /// <summary>Send a payload directly to a specific agent.</summary>
        public void DispatchTaskToAgent(string task, string agentId)
        {
            if (string.IsNullOrWhiteSpace(agentId)) return;
            comm.SendToAgent(agentId, task);
        }

        /// <summary>Very small “random” scheduler across the agents snapshot.</summary>
        public void DispatchTask(string task)
        {
            if (agents.Count == 0) return;
            // pick the first online agent for simplicity
            var target = agents.Values.FirstOrDefault(a => a.IsOnline && !a.AgentId.Equals("Master", StringComparison.OrdinalIgnoreCase))
                         ?? agents.Values.FirstOrDefault(a => !a.AgentId.Equals("Master", StringComparison.OrdinalIgnoreCase));
            if (target != null) DispatchTaskToAgent(task, target.AgentId);
        }

        public List<AgentStatus> GetAgentStatuses() => agents.Values.ToList();

        // (Optional) keep a basic mirror if the UI wants to push updates here
        public void UpdateFromConfig(AgentConfig cfg)
        {
            if (cfg == null || string.IsNullOrWhiteSpace(cfg.AgentId)) return;

            if (!agents.TryGetValue(cfg.AgentId, out var row))
            {
                row = new AgentStatus { AgentId = cfg.AgentId };
                agents[cfg.AgentId] = row;
            }

            row.CpuLogicalCores = cfg.CpuLogicalCores;
            row.HasGpu = cfg.HasGpu;
            row.GpuModel = cfg.GpuModel ?? "";
            row.GpuMemoryMB = cfg.GpuMemoryMB ?? 0;
            row.GpuCount = cfg.GpuCount ?? 0;
            row.InstanceId = cfg.InstanceId ?? "";
            row.IsOnline = true;
            row.LastHeartbeat = DateTime.Now;
        }

        public void UpdateFromPulse(AgentPulse p)
        {
            if (p == null || string.IsNullOrWhiteSpace(p.AgentId)) return;

            if (!agents.TryGetValue(p.AgentId, out var row))
            {
                row = new AgentStatus { AgentId = p.AgentId };
                agents[p.AgentId] = row;
            }

            row.CpuUsagePercent = Clamp01to100(p.CpuUsagePercent);
            row.MemoryAvailableMB = p.MemoryAvailableMB;
            row.GpuUsagePercent = Clamp01to100(p.GpuUsagePercent);
            row.TaskQueueLength = p.TaskQueueLength;
            row.NetworkMbps = p.NetworkMbps;
            row.DiskReadMBps = p.DiskReadMBps;
            row.DiskWriteMBps = p.DiskWriteMBps;
            row.LastHeartbeat = p.LastHeartbeat == default ? DateTime.Now : p.LastHeartbeat;
            row.IsOnline = true;
        }

        private static float Clamp01to100(float v) =>
            (float)Math.Max(0, Math.Min(100, v));
    }
}








