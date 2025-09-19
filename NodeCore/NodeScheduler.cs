using System;
using System.Collections.Generic;
using System.Linq;
using NodeCore;

namespace NodeCore.Scheduling
{
    public enum SchedulerMode
    {
        Auto,
        Manual,
        Affinity,
        RoundRobin,
        LeastBusy
    }

    public class NodeScheduler
    {
        private SchedulerMode mode = SchedulerMode.Auto;
        private List<AgentStatus> agents;
        private int roundRobinIndex = 0;

        public NodeScheduler(List<AgentStatus> agents)
        {
            this.agents = agents;
        }

        public void SetMode(SchedulerMode newMode)
        {
            mode = newMode;
        }

        public string SelectAgent(string taskType, string manualChoice = "")
        {
            switch (mode)
            {
                case SchedulerMode.Manual:
                    return manualChoice;

                case SchedulerMode.Affinity:
                    return agents
                        .Where(a => a.IsOnline && !a.IsBusy && MatchesAffinity(a, taskType))
                        .OrderBy(a => a.CpuUsagePercent + a.GpuUsagePercent)
                        .FirstOrDefault()?.AgentId ?? "None";

                case SchedulerMode.RoundRobin:
                    var eligible = agents.Where(a => a.IsOnline).ToList();
                    if (eligible.Count == 0) return "None";
                    var agent = eligible[roundRobinIndex % eligible.Count];
                    roundRobinIndex++;
                    return agent.AgentId;

                case SchedulerMode.LeastBusy:
                    return agents
                        .Where(a => a.IsOnline && !a.IsBusy)
                        .OrderBy(a => a.TaskQueueLength)
                        .FirstOrDefault()?.AgentId ?? "None";

                case SchedulerMode.Auto:
                default:
                    return agents
                        .Where(a => a.IsOnline && !a.IsBusy)
                        .OrderByDescending(ScoreAgent)
                        .FirstOrDefault()?.AgentId ?? "None";
            }
        }

        private bool MatchesAffinity(AgentStatus agent, string taskType)
        {
            // Stub: match based on taskType and agent capabilities
            return true;
        }

        private int ScoreAgent(AgentStatus status)
        {
            int score = 0;
            score += (int)(100 - status.CpuUsagePercent);
            score += (int)(100 - status.GpuUsagePercent);
            score += (int)(status.MemoryAvailableMB / 100);
            score -= status.TaskQueueLength * 10;
            return score;
        }
    }
}


