using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using NodeComm;
using NodeCore;

namespace NodeAgent
{
    public class NodeAgent
    {
        public string Id { get; private set; }
        private readonly CommChannel comm;
        private readonly System.Timers.Timer heartbeatTimer;
        private int taskQueueLength = 0;
        private TimeSpan lastTaskDuration = TimeSpan.Zero;
        private readonly Dictionary<string, CancellationTokenSource> taskCancellationTokens = new(); // Track tasks by ID for cancellation

        public NodeAgent(string id, CommChannel comm)
        {
            Id = id;
            this.comm = comm;
            comm.Register(id, ReceiveMessage);
            heartbeatTimer = new System.Timers.Timer(5000);
            heartbeatTimer.Elapsed += (s, e) => SendHeartbeat();
            heartbeatTimer.Start();
        }

        public void ExecuteTask(string taskId, string task)
        {
            var cts = new CancellationTokenSource();
            taskCancellationTokens[taskId] = cts;
            taskQueueLength++;
            var start = DateTime.Now;
            Console.WriteLine($"Agent {Id} executing task {taskId}: {task}");
            try
            {
                if (task.StartsWith("add:"))
                {
                    var parts = task.Split(':');
                    if (parts.Length == 3 && int.TryParse(parts[1], out int startNum) && int.TryParse(parts[2], out int end))
                    {
                        int sum = 0;
                        for (int i = startNum; i <= end && !cts.Token.IsCancellationRequested; i++)
                        {
                            sum += i;
                            Thread.Sleep(1); // Small delay to allow cancellation
                        }
                        if (!cts.Token.IsCancellationRequested)
                        {
                            comm.SendToMaster($"Result: {sum}");
                        }
                        else
                        {
                            comm.SendToMaster($"Interrupted: {taskId}");
                        }
                    }
                }
                else if (task.StartsWith("Launch:"))
                {
                    var parts = task.Split('|');
                    if (parts.Length == 2)
                    {
                        try
                        {
                            var process = System.Diagnostics.Process.Start(parts[1]);
                            if (process != null && !cts.Token.IsCancellationRequested)
                            {
                                process.WaitForExit();
                                comm.SendToMaster($"Launched: {parts[0].Split(':')[1]}");
                            }
                            else
                            {
                                process?.Kill();
                                comm.SendToMaster($"Interrupted: {taskId}");
                            }
                        }
                        catch (Exception ex)
                        {
                            comm.SendToMaster($"Launch failed: {ex.Message}");
                        }
                    }
                }
                else
                {
                    Thread.Sleep(500); // Simulate work
                }
                Console.WriteLine($"Agent {Id} completed task {taskId}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Agent {Id} task {taskId} failed: {ex.Message}");
                comm.SendToMaster($"Result: {taskId}: Error: {ex.Message}");
            }
            finally
            {
                lastTaskDuration = DateTime.Now - start;
                taskQueueLength--;
                taskCancellationTokens.Remove(taskId);
                if (cts != null)
                {
                    cts.Dispose();
                }
            }
        }

        private void ReceiveMessage(string message)
        {
            Console.WriteLine($"Agent {Id} received message: {message}");
            if (message.StartsWith("CustomTask:"))
            {
                var parts = message.Split(':');
                if (parts.Length > 2)
                {
                    var taskId = parts[1];
                    var description = string.Join(":", parts, 2, parts.Length - 2);
                    ExecuteTask(taskId, description);
                }
            }
            else if (message.StartsWith("StopTask:"))
            {
                var parts = message.Split(':');
                if (parts.Length > 1)
                {
                    var taskId = parts[1];
                    if (taskCancellationTokens.TryGetValue(taskId, out var cts))
                    {
                        cts.Cancel();
                        Console.WriteLine($"Agent {Id} cancelled task {taskId}");
                    }
                }
            }
            else if (message == "StopAllTasks")
            {
                foreach (var kvp in taskCancellationTokens)
                {
                    kvp.Value.Cancel();
                }
                taskCancellationTokens.Clear();
                taskQueueLength = 0;
                Console.WriteLine($"Agent {Id} cancelled all tasks");
            }
            else if (message.StartsWith("Ping"))
            {
                comm.SendToMaster($"PingReply:{Id}");
            }
            else
            {
                // Fallback to original ExecuteTask without ID
                ExecuteTask(Guid.NewGuid().ToString(), message);
            }
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
                DiskWriteMBps = GetDiskWriteSpeed(),
                HasGpu = TryDetectGpu(out string model, out int memoryMB),
                HasCuda = TryDetectCuda(),
                GpuModel = model,
                GpuMemoryMB = memoryMB
            };

            string json = status.ToJson();
            comm.SendToMaster(json);
        }

        private bool TryDetectGpu(out string model, out int memoryMB)
        {
            model = "No GPU detected";
            memoryMB = 0;
            try
            {
                var searcher = new ManagementObjectSearcher("select * from Win32_VideoController");
                foreach (var obj in searcher.Get())
                {
                    model = obj["Name"]?.ToString() ?? "Unknown GPU";
                    if (obj["AdapterRAM"] != null)
                    {
                        var memBytes = Convert.ToInt64(obj["AdapterRAM"]);
                        memoryMB = (int)(memBytes / (1024 * 1024));
                    }
                    return true;
                }
            }
            catch
            {
                model = "GPU detection failed";
                memoryMB = 0;
            }
            return false;
        }

        private bool TryDetectCuda()
        {
            try
            {
                // Stub for Pro version: Check CUDA availability
                return false;
            }
            catch
            {
                return false;
            }
        }

        private float GetCpuLoad()
        {
            try
            {
                using var counter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                counter.NextValue();
                Thread.Sleep(1000);
                return counter.NextValue();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GetCpuLoad failed: {ex.Message}");
                return new Random().Next(10, 90); // Fallback
            }
        }

        private float GetGpuLoad()
        {
            // Requires GPU-specific library (e.g., NVAPI for NVIDIA)
            return new Random().Next(5, 70); // Stub
        }

        private float GetAvailableMemory()
        {
            try
            {
                using var counter = new PerformanceCounter("Memory", "Available MBytes");
                return counter.NextValue();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GetAvailableMemory failed: {ex.Message}");
                return new Random().Next(1000, 8000); // Fallback
            }
        }

        private float GetNetworkSpeed()
        {
            // Stub: Use NetworkInterface.GetAllNetworkInterfaces()
            return new Random().Next(10, 500);
        }

        private float GetDiskReadSpeed()
        {
            // Stub: Use PerformanceCounter for disk read
            return new Random().Next(50, 300);
        }

        private float GetDiskWriteSpeed()
        {
            // Stub: Use PerformanceCounter for disk write
            return new Random().Next(40, 250);
        }

        private string[] GetAvailableFiles()
        {
            return new[] { "data1.bin", "log.txt", "config.json" };
        }
    }
}

