using System;
using System.Collections.Generic;
using System.Linq;
using NodeComm;
using NodeCore;
using NodeCore.Scheduling;

namespace NodeMaster
{
    public class NodeMaster
    {
        private readonly CommChannel comm;
        private Dictionary<string, AgentStatus> agents = new();
        private NodeScheduler scheduler;

        public NodeMaster(CommChannel comm)
        {
            this.comm = comm;
            comm.RegisterMaster(ReceiveAgentStatus);
        }

        // 🧠 Receive heartbeat from agent
        public void ReceiveAgentStatus(string json)
        {
            var status = AgentStatus.FromJson(json);
            agents[status.AgentId] = status;

            Console.WriteLine($"Heartbeat received from {status.AgentId} | CPU: {status.CpuUsagePercent}% | Tasks: {status.TaskQueueLength}");
        }

        // 📤 Dispatch task to specific agent
        public void DispatchTaskToAgent(string task, string agentId)
        {
            if (agents.TryGetValue(agentId, out var agent))
            {
                comm.SendToAgent(agentId, task);
                Console.WriteLine($"Dispatched task '{task}' to agent '{agentId}'");
            }
            else
            {
                Console.WriteLine($"Agent '{agentId}' not found — task not dispatched.");
            }
        }

        // 🧠 Dispatch task using scheduler
        public void DispatchTask(string task)
        {
            var agentList = agents.Values.ToList();
            scheduler = new NodeScheduler(agentList);

            string selectedAgentId = scheduler.SelectAgent(task);

            if (selectedAgentId != "None")
            {
                DispatchTaskToAgent(task, selectedAgentId);
            }
            else
            {
                Console.WriteLine("No suitable agent found for task dispatch.");
            }
        }

        // 📡 Register agent with master
        public void RegisterAgent(string agentId, Action<string> receiver)
        {
            comm.Register(agentId, receiver);
            Console.WriteLine($"Agent '{agentId}' registered with master.");
        }

        // 📊 Get current agent telemetry snapshot
        public List<AgentStatus> GetAgentStatuses()
        {
            return agents.Values.ToList();
        }
    }
}






