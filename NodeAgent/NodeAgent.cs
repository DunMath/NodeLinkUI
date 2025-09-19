using System;
using System.Timers;
using System.Threading;
using NodeComm;
using NodeCore;

namespace NodeAgent
{
    public class NodeAgent
    {
        public string Id { get; private set; }
        private readonly CommChannel comm;
        private readonly System.Timers.Timer heartbeatTimer;

        // 🧠 Task tracking
        private int taskQueueLength = 0;
        private TimeSpan lastTaskDuration = TimeSpan.Zero;

        public NodeAgent(string id, CommChannel comm)
        {
            Id = id;
            this.comm = comm;

            // Register this agent with the communication channel
            comm.Register(id, ReceiveMessage);

            // Start heartbeat loop
            heartbeatTimer = new System.Timers.Timer(5000); // every 5 seconds
            heartbeatTimer.Elapsed += (s, e) => SendHeartbeat();
            heartbeatTimer.Start();
        }

        public void ExecuteTask(string task)
        {
            taskQueueLength++;
            var start = DateTime.Now;

            Console.WriteLine($"Agent {Id} executing: {task}");
            Thread.Sleep(500); // Simulate work
            Console.WriteLine($"Agent {Id} completed task.");

            lastTaskDuration = DateTime.Now - start;
            taskQueueLength--;
        }

        private void ReceiveMessage(string message)
        {
            Console.WriteLine($"Agent {Id} received message: {message}");
            ExecuteTask(message); // Trigger task execution
        }

        private void SendHeartbeat()
        {
            var status = new AgentStatus
            {
                AgentId = Id,
                LastHeartbeat = DateTime.Now,
                IsOnline = true,
                CpuUsagePercent = GetCpuLoad(),
                GpuUsagePercent = GetGpuLoad(),
                MemoryAvailableMB = GetAvailableMemory(),
                NetworkMbps = GetNetworkSpeed(),
                HasFileAccess = true,
                AvailableFiles = GetAvailableFiles(),
                TaskQueueLength = taskQueueLength,
                LastTaskDuration = lastTaskDuration,
                DiskReadMBps = GetDiskReadSpeed(),
                DiskWriteMBps = GetDiskWriteSpeed()
            };

            string json = status.ToJson();
            comm.SendToMaster(json);
        }

        // 🧪 Stubbed telemetry methods — replace with real system calls later
        private float GetCpuLoad() => new Random().Next(10, 90);
        private float GetGpuLoad() => new Random().Next(5, 70);
        private float GetAvailableMemory() => new Random().Next(1000, 8000);
        private float GetNetworkSpeed() => new Random().Next(10, 500);
        private float GetDiskReadSpeed() => new Random().Next(50, 300);
        private float GetDiskWriteSpeed() => new Random().Next(40, 250);
        private string[] GetAvailableFiles() => new[] { "data1.bin", "log.txt", "config.json" };
    }
}

