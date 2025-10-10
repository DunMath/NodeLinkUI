// NodeAgent/NodeAgent.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using NodeComm;
using NodeCore;

namespace NodeAgent
{
    /// <summary>
    /// Lightweight agent runtime that:
    ///  - Listens for RegisterRequest and replies with RegisterAck (caps as JSON).
    ///  - Starts heartbeats only after successful registration.
    ///  - Emits static AgentConfig (rare) and volatile AgentPulse (frequent).
    ///  - Executes simple CustomTask/Compute messages with cancellation & result reporting.
    /// </summary>
    public sealed class NodeAgent
    {
        // ------------ Public identity ------------
        public string Id { get; }

        // ------------ Comms ------------
        private readonly CommChannel comm;

        // ------------ Registration state ------------
        private volatile bool _isRegistered = false;
        private string? _managerId = null;
        private DateTime _lastRegisterAck = DateTime.MinValue;

        // ------------ Heartbeat ------------
        private readonly System.Timers.Timer heartbeatTimer;
        private readonly string _instanceId = Guid.NewGuid().ToString();

        // ------------ Task bookkeeping ------------
        private readonly Dictionary<string, CancellationTokenSource> taskCancellationTokens = new();
        private volatile int taskQueueLength = 0;
        private TimeSpan lastTaskDuration = TimeSpan.Zero;

        // ------------ JSON options ------------
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        public NodeAgent(string id, CommChannel comm)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            this.comm = comm ?? throw new ArgumentNullException(nameof(comm));

            // Subscribe to the shared CommChannel; only handle messages routed to this agent id.
            this.comm.OnMessageReceived += (targetId, message) =>
            {
                try
                {
                    if (string.Equals(targetId, Id, StringComparison.OrdinalIgnoreCase))
                        ReceiveMessage(message);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Agent {Id} OnMessageReceived error: {ex.Message}");
                }
            };

            // Heartbeat (disabled until registration completes)
            heartbeatTimer = new System.Timers.Timer(5000);
            heartbeatTimer.AutoReset = true;
            heartbeatTimer.Elapsed += (s, e) => SendPulse();
            // Important: do NOT Start here; we start after RegisterAck.
        }

        // =====================================================================
        // Message handling
        // =====================================================================
        private void ReceiveMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            Console.WriteLine($"Agent {Id} received: {message}");

            try
            {
                // -------- Register handshake --------
                if (message.StartsWith("RegisterRequest:", StringComparison.OrdinalIgnoreCase))
                {
                    _managerId = message.Substring("RegisterRequest:".Length).Trim();
                    _isRegistered = true;

                    SendRegisterAck();           // reply with capabilities (legacy, still useful)
                    SendAgentConfig();           // static config once on register
                    SendPulse();                 // immediate first pulse
                    if (!heartbeatTimer.Enabled) // begin pulses
                        heartbeatTimer.Start();

                    // Optional quick pong for UX
                    comm.SendToMaster($"PingReply:{Id}");
                    return;
                }

                // -------- NEW: Ping with master identity (from broadcast) --------
                // Expected format: Ping:<MasterId>:<MasterIp>:<Seq>
                if (message.StartsWith("Ping:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = message.Split(':');
                    if (parts.Length >= 4)
                    {
                        var masterId = parts[1].Trim();
                        var masterIp = parts[2].Trim();
                        var seq = parts[3].Trim();

                        // Teach CommChannel where to unicast replies
                        comm.SetMasterIp(masterIp);

                        // Immediately reply via UNICAST so Master can lock onto us fast
                        comm.SendToMaster($"Pong:{Id}:{seq}");
                        SendAgentConfig();
                        SendPulse();

                        // If we weren't "registered" via RegisterRequest yet, allow pulses to start now
                        if (!_isRegistered)
                        {
                            _managerId = masterId;
                            _isRegistered = true;
                            if (!heartbeatTimer.Enabled) heartbeatTimer.Start();
                        }
                        return;
                    }

                    // If format didn't match, fall through to generic Ping handler below
                }

                // -------- Keep-alive (generic Ping) --------
                if (message.StartsWith("Ping", StringComparison.OrdinalIgnoreCase))
                {
                    comm.SendToMaster($"PingReply:{Id}");
                    return;
                }

                // -------- Master asks for fresh static config --------
                if (string.Equals(message, "RequestConfig", StringComparison.OrdinalIgnoreCase))
                {
                    SendAgentConfig();
                    return;
                }

                // -------- Master solicits presence from already running agents --------
                if (string.Equals(message, "WhoIsAlive", StringComparison.OrdinalIgnoreCase))
                {
                    // Even if not "registered" via RegisterRequest yet, answer with config+pulse
                    SendAgentConfig();
                    SendPulse();
                    return;
                }

                // -------- ComputeTest (tiny synthetic work; Master checks these results) --------
                if (message.StartsWith("ComputeTest:", StringComparison.OrdinalIgnoreCase))
                {
                    var testId = message.Substring("ComputeTest:".Length).Trim();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            long acc = 0;
                            for (int i = 1; i <= 50; i++) acc += i;
                            await Task.Delay(10);

                            // Send a clear, explicit result back to Master (UNICAST)
                            comm.SendToMaster($"ComputeTestResult:{Id}:{testId}:OK");
                        }
                        catch
                        {
                            comm.SendToMaster($"ComputeTestResult:{Id}:{testId}:FAIL");
                        }
                    });
                    return;
                }

                // -------- Compute (very small demo workload with ordered result) --------
                if (message.StartsWith("Compute:", StringComparison.OrdinalIgnoreCase))
                {
                    var body = message.Substring("Compute:".Length);
                    var tokens = body.Split('|'); // app|seq|payload
                    if (tokens.Length >= 2)
                    {
                        var app = tokens[0];
                        var seq = tokens[1];
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

                // -------- CustomTask (ad-hoc string payload) --------
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

                // -------- Stop one task --------
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

                // -------- Stop all tasks --------
                if (string.Equals(message, "StopAllTasks", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var kvp in taskCancellationTokens)
                        kvp.Value.Cancel();
                    taskCancellationTokens.Clear();
                    taskQueueLength = 0;
                    Console.WriteLine($"Agent {Id} cancelled all tasks");
                    return;
                }

                // Fallback: treat message as a generic task
                ExecuteTask(Guid.NewGuid().ToString("N"), message);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Agent {Id} ReceiveMessage error: {ex.Message}");
                comm.SendToMaster($"Result:{Id}: Error: {ex.Message}");
            }
        }

        // =====================================================================
        // Register ACK (legacy capabilities snapshot)
        // =====================================================================
        private void SendRegisterAck()
        {
            var caps = new
            {
                agent_id = Id,
                instance_id = _instanceId,
                cpu_cores = Environment.ProcessorCount,

                has_gpu = TryDetectGpu(out string gpuModel, out int gpuMemMB, out int gpuCount),
                gpu_model = gpuModel,
                gpu_mem_mb = gpuMemMB,
                gpu_count = gpuCount,

                heartbeat_ms = (int)heartbeatTimer.Interval
            };

            string json = JsonSerializer.Serialize(caps, JsonOpts);
            comm.SendToMaster($"RegisterAck:{Id}:{json}");
            _lastRegisterAck = DateTime.UtcNow;
        }

        // =====================================================================
        // Static config (rare)
        // =====================================================================
        private void SendAgentConfig()
        {
            var cfg = new AgentConfig
            {
                AgentId = Id,
                CpuLogicalCores = Environment.ProcessorCount,
                RamTotalMB = GetTotalPhysicalRamMB(),
                HasGpu = TryDetectGpu(out string model, out int memMB, out int gpuCount),
                GpuModel = model,
                GpuMemoryMB = memMB,
                GpuCount = gpuCount,
                OsVersion = Environment.OSVersion.ToString(),
                InstanceId = _instanceId
            };

            var json = JsonSerializer.Serialize(cfg, JsonOpts);
            comm.SendToMaster($"AgentConfig:{json}");
        }

        // =====================================================================
        // Volatile pulse (frequent)
        // =====================================================================
        private void SendPulse()
        {
            if (!_isRegistered)
                return; // keep quiet until we're officially registered (keeps noise down)

            var pulse = new AgentPulse
            {
                AgentId = Id,
                CpuUsagePercent = GetCpuLoad(),
                MemoryAvailableMB = GetAvailableMemory(),
                GpuUsagePercent = GetGpuLoad(),
                TaskQueueLength = taskQueueLength,
                NetworkMbps = GetNetworkSpeed(),
                DiskReadMBps = 0f,
                DiskWriteMBps = 0f,
                LastHeartbeat = DateTime.Now
            };

            var json = JsonSerializer.Serialize(pulse, JsonOpts);
            comm.SendToMaster($"AgentPulse:{json}");
        }

        // =====================================================================
        // Task execution (toy implementation with cancellation)
        // =====================================================================
        private void ExecuteTask(string taskId, string description)
        {
            var cts = new CancellationTokenSource();
            taskCancellationTokens[taskId] = cts;
            Interlocked.Increment(ref taskQueueLength);

            _ = Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    // Simulate short work chunking to make cancellation responsive
                    for (int i = 0; i < 20; i++)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        await Task.Delay(50, cts.Token);
                    }

                    comm.SendToMaster($"Result:{Id}: Completed task {taskId}: {description}");
                }
                catch (OperationCanceledException)
                {
                    comm.SendToMaster($"Result:{Id}: Cancelled task {taskId}");
                }
                catch (Exception ex)
                {
                    comm.SendToMaster($"Result:{Id}: Error in task {taskId}: {ex.Message}");
                }
                finally
                {
                    sw.Stop();
                    lastTaskDuration = sw.Elapsed;
                    taskCancellationTokens.Remove(taskId);
                    Interlocked.Decrement(ref taskQueueLength);
                }
            }, cts.Token);
        }

        // =====================================================================
        // System probes (lightweight)
        // =====================================================================
        private static string? GetLocalIPAddress()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var props = ni.GetIPProperties();
                    foreach (var ip in props.UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            return ip.Address.ToString();
                    }
                }
            }
            catch { /* ignore */ }
            return null;
        }

        private static float GetCpuLoad()
        {
            // TODO: port MainWindow's accurate CPU sampling to NodeAgent for parity.
            try
            {
                return (float)Math.Max(0, Math.Min(100, new Random().Next(10, 40))); // placeholder sample
            }
            catch { return 0f; }
        }

        private static float GetGpuLoad()
        {
            // TODO: port GPU Engine counter sampling if desired. Keep light for now.
            try { return 0f; } catch { return 0f; }
        }

        private static float GetAvailableMemory()
        {
            try
            {
                // TODO: implement a real available memory probe; keep simple for now.
                return 0f;
            }
            catch { return 0f; }
        }

        private static float GetNetworkSpeed() => 0f;

        private static bool TryDetectGpu(out string model, out int memMB, out int gpuCount)
        {
            model = string.Empty;
            memMB = 0;
            gpuCount = 0;
            try
            {
                using var s = new ManagementObjectSearcher("SELECT Name, AdapterRAM, PNPDeviceID FROM Win32_VideoController");
                foreach (ManagementObject mo in s.Get())
                {
                    string name = mo["Name"]?.ToString() ?? "";
                    string pnp = mo["PNPDeviceID"]?.ToString() ?? "";
                    if (pnp.IndexOf("PCI", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (name.IndexOf("Microsoft Basic Display", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    long bytes = 0;
                    var ramObj = mo["AdapterRAM"];
                    if (ramObj != null) { try { bytes = Convert.ToInt64(ramObj); } catch { bytes = 0; } }

                    gpuCount++;
                    if (string.IsNullOrEmpty(model)) model = name;
                    if (memMB == 0 && bytes > 0) memMB = (int)Math.Round(bytes / (1024.0 * 1024.0));
                }

                return gpuCount > 0;
            }
            catch
            {
                return false;
            }
        }

        private static float GetTotalPhysicalRamMB()
        {
            try
            {
                // Avoid Microsoft.VisualBasic dependency; use WMI.
                using var cs = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
                foreach (ManagementObject mo in cs.Get())
                {
                    if (mo["TotalVisibleMemorySize"] != null)
                    {
                        // value is in KB
                        double kb = Convert.ToDouble(mo["TotalVisibleMemorySize"]);
                        return (float)(kb / 1024.0);
                    }
                }
            }
            catch { }
            return 0f;
        }
    }
}





