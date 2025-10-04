using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using NodeComm;
using NodeCore;

namespace NodeAgent
{
    public class NodeAgent : IDisposable
    {
        public string Id { get; private set; }
        private readonly CommChannel comm;
        private readonly System.Timers.Timer heartbeatTimer;
        private int taskQueueLength = 0;
        private TimeSpan lastTaskDuration = TimeSpan.Zero;
        private readonly Dictionary<string, CancellationTokenSource> taskCancellationTokens = new(); // Track tasks by ID for cancellation
        private readonly string _instanceId = Guid.NewGuid().ToString();

        public NodeAgent(string id, CommChannel comm)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            this.comm = comm ?? throw new ArgumentNullException(nameof(comm));

            // Register receive handler for this agent id
            comm.Register(id, ReceiveMessage);

            heartbeatTimer = new System.Timers.Timer(5000);
            heartbeatTimer.Elapsed += (s, e) => SendHeartbeat();
            heartbeatTimer.Start();
        }

        // ------------- Message handling -------------

        private void ReceiveMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            Console.WriteLine($"Agent {Id} received message: {message}");

            try
            {
                if (message.StartsWith("CustomTask:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = message.Split(':');
                    if (parts.Length > 2)
                    {
                        var taskId = parts[1];
                        var description = string.Join(":", parts, 2, parts.Length - 2);
                        ExecuteTask(taskId, description);
                    }
                    return;
                }

                if (message.StartsWith("StopTask:", StringComparison.OrdinalIgnoreCase))
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
                    return;
                }

                if (string.Equals(message, "StopAllTasks", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var kvp in taskCancellationTokens)
                        kvp.Value.Cancel();

                    taskCancellationTokens.Clear();
                    taskQueueLength = 0;
                    Console.WriteLine($"Agent {Id} cancelled all tasks");
                    return;
                }

                if (message.StartsWith("Ping", StringComparison.OrdinalIgnoreCase))
                {
                    comm.SendToMaster($"PingReply:{Id}");
                    return;
                }

                // ---- New: compute test endpoint (exercises the real compute pipe) ----
                if (message.StartsWith("ComputeTest:", StringComparison.OrdinalIgnoreCase))
                {
                    var testId = message.Substring("ComputeTest:".Length).Trim();
                    _ = Task.Run(() => HandleComputeTestAsync(testId));
                    return;
                }

                // ---- New: handle "Compute:{app}|{seq}|{payload}" and report back ----
                if (message.StartsWith("Compute:", StringComparison.OrdinalIgnoreCase))
                {
                    var body = message.Substring("Compute:".Length);
                    // Expected: app|seq|payloadId
                    var tokens = body.Split('|');
                    if (tokens.Length >= 2)
                    {
                        var app = tokens[0];
                        var seq = tokens[1];

                        // Simulate some quick work to exercise the pipe
                        _ = Task.Run(async () =>
                        {
                            long acc = 0;
                            for (int i = 1; i <= 5000; i++) acc += i;
                            await Task.Delay(30);
                            comm.SendToMaster($"ComputeResult:{app}|{seq}|OK:{Id}");
                        });
                        return;
                    }
                }

                // Fallback to original ExecuteTask without ID
                ExecuteTask(Guid.NewGuid().ToString(), message);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Agent {Id} ReceiveMessage error: {ex.Message}");
                comm.SendToMaster($"Result:{Id}: Error: {ex.Message}");
            }
        }

        private async Task HandleComputeTestAsync(string testId)
        {
            try
            {
                // Do a tiny bit of “work”
                long acc = 0;
                for (int i = 1; i <= 10000; i++) acc += i;
                await Task.Delay(50);

                comm.SendToMaster($"ComputeTestResult:{Id}:{testId}:OK");
            }
            catch
            {
                comm.SendToMaster($"ComputeTestResult:{Id}:{testId}:FAIL");
            }
        }

        // ------------- Generic Task Execution -------------

        public void ExecuteTask(string taskId, string task)
        {
            var cts = new CancellationTokenSource();
            taskCancellationTokens[taskId] = cts;
            taskQueueLength++;
            var start = DateTime.Now;

            Console.WriteLine($"Agent {Id} executing task {taskId}: {task}");

            try
            {
                if (task.StartsWith("add:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = task.Split(':');
                    if (parts.Length == 3 &&
                        int.TryParse(parts[1], out int startNum) &&
                        int.TryParse(parts[2], out int end))
                    {
                        int sum = 0;
                        for (int i = startNum; i <= end && !cts.Token.IsCancellationRequested; i++)
                        {
                            sum += i;
                            Thread.Sleep(1); // allow cancellation
                        }

                        if (!cts.Token.IsCancellationRequested)
                            comm.SendToMaster($"Result:{taskId}: {sum}");
                        else
                            comm.SendToMaster($"Interrupted:{taskId}");
                    }
                }
                else if (task.StartsWith("Launch:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = task.Split('|');
                    if (parts.Length == 2)
                    {
                        try
                        {
                            var process = Process.Start(parts[1]);
                            if (process != null && !cts.Token.IsCancellationRequested)
                            {
                                process.WaitForExit();
                                comm.SendToMaster($"Launched:{parts[0].Split(':')[1]}");
                            }
                            else
                            {
                                process?.Kill();
                                comm.SendToMaster($"Interrupted:{taskId}");
                            }
                        }
                        catch (Exception ex)
                        {
                            comm.SendToMaster($"Launch failed:{ex.Message}");
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
                comm.SendToMaster($"Result:{taskId}: Error: {ex.Message}");
            }
            finally
            {
                lastTaskDuration = DateTime.Now - start;
                taskQueueLength--;
                if (taskCancellationTokens.Remove(taskId, out var tok))
                    tok.Dispose();
            }
        }

        // ------------- Heartbeat -------------

        private void SendHeartbeat()
        {
            var status = new AgentStatus
            {
                AgentId = Id,
                LastHeartbeat = DateTime.Now,
                IsOnline = true,
                IpAddress = GetLocalIPAddress() ?? string.Empty,

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

                CpuLogicalCores = Environment.ProcessorCount,

                HasGpu = TryDetectGpu(out string model, out int memoryMB, out int gpuCount),
                HasCuda = TryDetectCuda(),
                GpuModel = model,
                GpuMemoryMB = memoryMB,

                // Pro/upsell fields
                GpuCount = gpuCount,
                InstanceId = _instanceId
            };

            string json = status.ToJson();
            // IMPORTANT: Master expects "AgentStatus:{json}"
            comm.SendToMaster($"AgentStatus:{json}");
        }

        // ------------- Hardware helpers -------------

        private bool TryDetectGpu(out string model, out int memoryMB, out int gpuCount)
        {
            model = "No GPU detected";
            memoryMB = 0;
            gpuCount = 0;

            try
            {
                var searcher = new ManagementObjectSearcher("select * from Win32_VideoController");
                foreach (var obj in searcher.Get())
                {
                    gpuCount++;
                    // use the first as “primary” for display
                    if (gpuCount == 1)
                    {
                        model = obj["Name"]?.ToString() ?? "Unknown GPU";
                        if (obj["AdapterRAM"] != null)
                        {
                            var memBytes = Convert.ToInt64(obj["AdapterRAM"]);
                            memoryMB = (int)(memBytes / (1024 * 1024));
                        }
                    }
                }

                return gpuCount > 0;
            }
            catch
            {
                model = "GPU detection failed";
                memoryMB = 0;
                gpuCount = 0;
                return false;
            }
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
                Thread.Sleep(250);
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
            // Requires GPU-specific library (e.g., NVAPI for NVIDIA) – keep stubbed
            return new Random().Next(5, 70);
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
            // Stub: could measure using NetworkInterface statistics
            return new Random().Next(10, 500);
        }

        private float GetDiskReadSpeed()
        {
            // Stub: could read from perf counters
            return new Random().Next(50, 300);
        }

        private float GetDiskWriteSpeed()
        {
            // Stub: could read from perf counters
            return new Random().Next(40, 250);
        }

        private string[] GetAvailableFiles()
        {
            return new[] { "data1.bin", "log.txt", "config.json" };
        }

        private static string? GetLocalIPAddress()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;

                    var ipProps = ni.GetIPProperties();
                    foreach (var ua in ipProps.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            return ua.Address.ToString();
                    }
                }
            }
            catch { /* ignore */ }
            return null;
        }

        public void Dispose()
        {
            try { heartbeatTimer?.Stop(); } catch { /* ignore */ }
            try { heartbeatTimer?.Dispose(); } catch { /* ignore */ }

            foreach (var kvp in taskCancellationTokens)
                kvp.Value.Cancel();
            taskCancellationTokens.Clear();
        }
    }
}


