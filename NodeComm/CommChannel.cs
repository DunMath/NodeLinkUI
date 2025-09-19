using System;
using System.Collections.Generic;

namespace NodeComm
{
    public class CommChannel
    {
        // 🔁 Agent message routing
        private Dictionary<string, Action<string>> agentReceivers = new();

        // 🧠 Master heartbeat receiver
        private Action<string> masterReceiver;

        // 📡 Register an agent to receive messages
        public void Register(string agentId, Action<string> receiver)
        {
            agentReceivers[agentId] = receiver;
        }

        // 📡 Register the master to receive heartbeats
        public void RegisterMaster(Action<string> receiver)
        {
            masterReceiver = receiver;
        }

        // 📤 Send a message to a specific agent
        public void SendToAgent(string agentId, string message)
        {
            if (agentReceivers.TryGetValue(agentId, out var receiver))
            {
                receiver?.Invoke(message);
            }
            else
            {
                Console.WriteLine($"CommChannel: Agent '{agentId}' not found.");
            }
        }
        public void Broadcast(string message)
        {
            foreach (var receiver in agentReceivers.Values)
            {
                receiver?.Invoke(message);
            }
        }


        // 📥 Send a heartbeat or telemetry to the master
        public void SendToMaster(string message)
        {
            if (masterReceiver != null)
            {
                masterReceiver.Invoke(message);
            }
            else
            {
                Console.WriteLine("CommChannel: No master registered to receive message.");
            }
        }
    }
}



