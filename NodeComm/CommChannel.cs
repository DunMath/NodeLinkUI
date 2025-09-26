using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Makaretu.Dns; // For mDNS support

namespace NodeComm
{
    public class CommChannel
    {
        private Dictionary<string, Action<string>> agentReceivers = new();
        private Action<string> masterReceiver;
        public event Action<string, string> OnMessageReceived;

        public CommChannel()
        {
            // Initialize mDNS or TCP (stub for now)
        }

        public void Register(string agentId, Action<string> receiver)
        {
            agentReceivers[agentId] = receiver;
            // Bridge to OnMessageReceived
            OnMessageReceived += (id, message) => { if (id == agentId) receiver?.Invoke(message); };
        }

        public void RegisterMaster(Action<string> receiver)
        {
            masterReceiver = receiver;
            // Bridge to OnMessageReceived
            OnMessageReceived += (id, message) => { if (id == "Master") receiver?.Invoke(message); };
        }

        public void SendToAgent(string agentId, string message)
        {
            if (agentReceivers.TryGetValue(agentId, out var receiver))
            {
                Task.Run(() =>
                {
                    receiver?.Invoke(message);
                    OnMessageReceived?.Invoke(agentId, message); // Notify event subscribers
                });
            }
            else
            {
                Console.WriteLine($"CommChannel: Agent '{agentId}' not found.");
            }
        }

        public async Task SendToAgentAsync(string agentId, string message)
        {
            await Task.Run(() => SendToAgent(agentId, message)); // Network stub for TCP/UDP
        }

        public void Broadcast(string message)
        {
            foreach (var receiver in agentReceivers.Values)
            {
                Task.Run(() =>
                {
                    receiver?.Invoke(message);
                    OnMessageReceived?.Invoke("Broadcast", message); // Notify event subscribers
                });
            }
        }

        public void SendToMaster(string message)
        {
            if (masterReceiver != null)
            {
                Task.Run(() =>
                {
                    masterReceiver.Invoke(message);
                    OnMessageReceived?.Invoke("Master", message); // Notify event subscribers
                });
            }
            else
            {
                Console.WriteLine("CommChannel: No master registered to receive message.");
            }
        }

        public async Task SendToMasterAsync(string message)
        {
            await Task.Run(() => SendToMaster(message)); // Network stub for TCP/UDP
        }
    }
}



