using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NodeCore;

namespace NodeAgent
{
    // Matches the adapter you wired in MainWindow (INodeComm.SendToMaster)
    public interface INodeComm
    {
        void SendToMaster(string payload);
    }

    /// <summary>
    /// Minimal HTTP over raw TcpListener (no HttpListener, no URL ACL).
    /// Routes (both plain and /nl/* work):
    ///   GET  / or /nl                 -> { AgentId, Status, Utc }
    ///   GET  /health or /nl/health    -> "OK"
    ///   GET  /info or /nl/info        -> AgentConfig JSON
    ///   POST /register or /nl/register-> sends RegisterAck to master, returns JSON { Ack = true }
    ///
    ///   POST /compute                 -> queue work (string body); returns { accepted, taskId }
    ///   GET  /tasks/next              -> debug: peek the next queued task (non-destructive)
    ///   POST /task/result             -> optional manual result reporting { taskId, status, detail }
    ///
    /// A lightweight background executor dequeues items from /compute and simulates
    /// completing them; it sends "ComputeResult:<payload>:OK" back to the master.
    /// </summary>
    public sealed class AgentBootstrapper
    {
        public const int AgentHttpPort = 50555;

        private readonly INodeComm _comm;
        private readonly string _agentId;

        private TcpListener _tcp;
        private CancellationTokenSource _cts;
        private Task _acceptLoop;
        private volatile bool _isListening;

        // --- simple in-process task queue + executor ---
        private readonly ConcurrentQueue<(string TaskId, string Payload, DateTime EnqueuedUtc)> _tasks
            = new ConcurrentQueue<(string, string, DateTime)>();

        private readonly SemaphoreSlim _taskSignal = new SemaphoreSlim(0, int.MaxValue);
        private Task _execLoop;

        public bool IsListening => _isListening;

        public AgentBootstrapper(INodeComm comm, string agentId)
        {
            _comm = comm ?? throw new ArgumentNullException(nameof(comm));
            _agentId = string.IsNullOrWhiteSpace(agentId) ? $"Agent-{Environment.MachineName}" : agentId;
        }

        /// <summary>
        /// Start socket listener and advertise ourselves to master (config + pulse).
        /// </summary>
        public void Start(string masterIp)
        {
            FileLogger.Info($"AgentBootstrapper.Start(masterIp={masterIp}) for {_agentId}");
            _comm.SendToMaster($"AgentBoot:START:{_agentId}:{DateTimeOffset.UtcNow:O}");

            try
            {
                StartTcpListener();
                _comm.SendToMaster($"AgentHttpStart:OK:{_agentId}:{DateTimeOffset.UtcNow:O}");
                FileLogger.Info($"TcpListener started on 0.0.0.0:{AgentHttpPort} (IsListening={_isListening})");
            }
            catch (Exception ex)
            {
                FileLogger.Error("Failed to start TcpListener", ex);
                _comm.SendToMaster($"AgentHttpStart:ERR:{_agentId}:{Sanitize(ex.Message)}");
            }

            // Always advertise ourselves so the grid lights up (even if HTTP failed).
            SendInitialConfigAndPulse();

            // background executor for /compute tasks
            _execLoop = Task.Run(() => ExecLoopAsync());
        }

        /// <summary>Stop the listener gracefully.</summary>
        public async Task StopAsync()
        {
            FileLogger.Info("AgentBootstrapper.StopAsync()");
            try
            {
                _cts?.Cancel();
                _taskSignal.Release(); // wake executor if waiting

                if (_tcp != null)
                {
                    try { _tcp.Stop(); } catch { }
                }

                var waits = new List<Task>();
                if (_acceptLoop != null) waits.Add(_acceptLoop);
                if (_execLoop != null) waits.Add(_execLoop);
                if (waits.Count > 0)
                    await Task.WhenAny(Task.WhenAll(waits), Task.Delay(1000)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                FileLogger.Warn("StopAsync swallow exception", ex);
            }
            finally
            {
                _tcp = null;
                _acceptLoop = null;
                _execLoop = null;
                _cts?.Dispose();
                _cts = null;
                _isListening = false;
                FileLogger.Info("AgentBootstrapper stopped.");
            }
        }

        private void StartTcpListener()
        {
            if (_tcp != null) return;

            _cts = new CancellationTokenSource();
            _tcp = new TcpListener(IPAddress.Any, AgentHttpPort);

            try
            {
                _tcp.Start();
                _isListening = true;
            }
            catch (SocketException ex)
            {
                throw new InvalidOperationException($"Failed to bind TCP on port {AgentHttpPort}: {ex.Message}", ex);
            }

            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            FileLogger.Info("AcceptLoopAsync entered");
            while (!token.IsCancellationRequested && _tcp != null)
            {
                TcpClient client = null;
                try
                {
                    client = await _tcp.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    FileLogger.Info("Listener disposed, exiting accept loop.");
                    break;
                }
                catch (InvalidOperationException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) break;
                    FileLogger.Warn("AcceptTcpClientAsync error; continuing.", ex);
                    continue;
                }

                if (client != null)
                {
                    _ = Task.Run(() => HandleClientAsync(client));
                }
            }
            FileLogger.Info("AcceptLoopAsync exited");
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "/";
            int q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);

            // Accept both /thing and /nl/thing
            if (path.Equals("/nl", StringComparison.OrdinalIgnoreCase)) return "/";
            if (path.StartsWith("/nl/", StringComparison.OrdinalIgnoreCase))
                path = path.Substring(3); // remove "/nl"
            return path;
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            {
                NetworkStream stream = null;
                StreamReader reader = null;
                StreamWriter writer = null;

                try
                {
                    stream = client.GetStream();
                    stream.ReadTimeout = 8000;
                    stream.WriteTimeout = 8000;

                    reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
                    writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                    {
                        NewLine = "\r\n",
                        AutoFlush = true
                    };

                    // --- Read request line ---
                    string reqLine = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (string.IsNullOrEmpty(reqLine)) return;

                    string[] reqParts = reqLine.Split(' ');
                    string method = (reqParts.Length > 0) ? reqParts[0] : "GET";
                    string rawPath = (reqParts.Length > 1) ? reqParts[1] : "/";
                    string path = NormalizePath(rawPath);

                    // --- Read headers ---
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string line;
                    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        if (line.Length == 0) break; // end of headers
                        int idx = line.IndexOf(':');
                        if (idx > 0)
                        {
                            string name = line.Substring(0, idx).Trim();
                            string value = line.Substring(idx + 1).Trim();
                            headers[name] = value;
                        }
                    }

                    // --- Optional body ---
                    int contentLength = 0;
                    if (headers.TryGetValue("Content-Length", out string clVal))
                    {
                        int.TryParse(clVal, out contentLength);
                    }

                    string body = "";
                    if (contentLength > 0)
                    {
                        var buf = new char[contentLength];
                        int read = 0;
                        while (read < contentLength)
                        {
                            int n = await reader.ReadAsync(buf, read, contentLength - read).ConfigureAwait(false);
                            if (n <= 0) break;
                            read += n;
                        }
                        body = new string(buf, 0, read);
                    }

                    FileLogger.Info($"REQ {method} {rawPath} -> {path} len={contentLength}");

                    // --- Route ---
                    if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) && (path == "/"))
                    {
                        var data = new { AgentId = _agentId, Status = "OK", Utc = DateTime.UtcNow };
                        await WriteJsonAsync(writer, data).ConfigureAwait(false);
                    }
                    else if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) && path == "/health")
                    {
                        await WriteTextAsync(writer, 200, "OK").ConfigureAwait(false);
                    }
                    else if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) && path == "/info")
                    {
                        var cfg = BuildAgentConfig();
                        await WriteJsonAsync(writer, cfg).ConfigureAwait(false);
                    }
                    else if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) && path == "/register")
                    {
                        // Master polls this to mark the agent as registered
                        _comm.SendToMaster($"RegisterAck:{_agentId}:{DateTimeOffset.UtcNow:O}");
                        FileLogger.Info("RegisterAck sent to master.");
                        await WriteJsonAsync(writer, new { AgentId = _agentId, Ack = true, WhenUtc = DateTime.UtcNow }).ConfigureAwait(false);
                    }
                    // ----- compute endpoints -----
                    else if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) && path == "/compute")
                    {
                        // Body is the payload to "compute".
                        string taskId = Guid.NewGuid().ToString("N");
                        _tasks.Enqueue((taskId, body ?? "", DateTime.UtcNow));
                        _taskSignal.Release();
                        FileLogger.Info($"Enqueued task {taskId} payloadLen={(body ?? "").Length}");
                        await WriteJsonAsync(writer, new { accepted = true, taskId }).ConfigureAwait(false);
                    }
                    else if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) && path == "/tasks/next")
                    {
                        // Non-destructive peek for diagnostics
                        if (_tasks.TryPeek(out var next))
                            await WriteJsonAsync(writer, new { hasItem = true, next.TaskId, next.Payload, next.EnqueuedUtc }).ConfigureAwait(false);
                        else
                            await WriteJsonAsync(writer, new { hasItem = false }).ConfigureAwait(false);
                    }
                    else if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) && path == "/task/result")
                    {
                        // Optional manual callback path if some external runner posts results back to the agent first.
                        // Expect a small JSON payload.
                        try
                        {
                            var doc = JsonDocument.Parse(body ?? "{}");
                            string taskId = doc.RootElement.TryGetProperty("taskId", out var t) ? t.GetString() ?? "" : "";
                            string status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() ?? "OK" : "OK";
                            string detail = doc.RootElement.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "";
                            _comm.SendToMaster($"ComputeResult:{taskId}:{status}:{detail}");
                            FileLogger.Info($"Manual result posted for {taskId}: {status}");
                            await WriteJsonAsync(writer, new { ok = true }).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            FileLogger.Warn("Failed to parse /task/result body", ex);
                            await WriteTextAsync(writer, 400, "bad json").ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await WriteTextAsync(writer, 404, "Not Found").ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Error("HandleClientAsync error", ex);
                    try
                    {
                        if (writer != null)
                            await WriteTextAsync(writer, 500, "error:" + ex.Message).ConfigureAwait(false);
                    }
                    catch { /* ignore */ }
                }
                finally
                {
                    try { writer?.Flush(); } catch { }
                    try { stream?.Flush(); } catch { }
                    writer?.Dispose();
                    reader?.Dispose();
                    stream?.Dispose();
                }
            }
        }

        // --- tiny executor simulating compute ---
        private async Task ExecLoopAsync()
        {
            FileLogger.Info("ExecLoop started");
            while (_cts != null && !_cts.IsCancellationRequested)
            {
                try
                {
                    // Wait for a task or cancellation
                    await _taskSignal.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    if (_cts != null && _cts.IsCancellationRequested) break;

                    (string TaskId, string Payload, DateTime EnqueuedUtc) item;
                    while (_tasks.TryDequeue(out item))
                    {
                        // Simulate doing the work
                        FileLogger.Info($"Executing task {item.TaskId} …");
                        await Task.Delay(50).ConfigureAwait(false);

                        // Send a canonical result line that your Master already understands
                        // If payloads are of the form "QuickSmoke|0|noop" you’ll see the ticks appear.
                        _comm.SendToMaster($"ComputeResult:{item.Payload}:OK");
                        FileLogger.Info($"Task {item.TaskId} completed -> ComputeResult:{item.Payload}:OK");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    FileLogger.Warn("ExecLoop caught error; continuing.", ex);
                }
            }
            FileLogger.Info("ExecLoop exited");
        }

        // --- HTTP response helpers (keep simple, C# 12 safe) ---

        private static async Task WriteTextAsync(StreamWriter writer, int status, string text)
        {
            string statusText = status == 200 ? "OK" : (status == 404 ? "Not Found" : "ERR");
            byte[] bodyBytes = Encoding.UTF8.GetBytes(text ?? "");
            string headers =
                $"HTTP/1.1 {status} {statusText}\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\n" +
                "Connection: close\r\n\r\n";

            await writer.WriteAsync(headers).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
            await writer.BaseStream.WriteAsync(bodyBytes, 0, bodyBytes.Length).ConfigureAwait(false);
            await writer.BaseStream.FlushAsync().ConfigureAwait(false);
        }

        private static async Task WriteJsonAsync(StreamWriter writer, object data)
        {
            string json = JsonSerializer.Serialize(data);
            byte[] bodyBytes = Encoding.UTF8.GetBytes(json);
            string headers =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\n" +
                "Connection: close\r\n\r\n";

            await writer.WriteAsync(headers).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
            await writer.BaseStream.WriteAsync(bodyBytes, 0, bodyBytes.Length).ConfigureAwait(false);
            await writer.BaseStream.FlushAsync().ConfigureAwait(false);
        }

        // --- initial advertise ---

        private void SendInitialConfigAndPulse()
        {
            var cfg = BuildAgentConfig();
            FileLogger.Info("Sending initial AgentConfig and AgentPulse to master.");
            _comm.SendToMaster("AgentConfig:" + JsonSerializer.Serialize(cfg));

            var pulse = new
            {
                AgentId = _agentId,
                CpuUsagePercent = 0f,
                MemoryAvailableMB = 0f,
                GpuUsagePercent = 0f,
                TaskQueueLength = 0,
                NetworkMbps = 0f,
                DiskReadMBps = 0f,
                DiskWriteMBps = 0f,
                LastHeartbeat = DateTime.Now
            };
            _comm.SendToMaster("AgentPulse:" + JsonSerializer.Serialize(pulse));
        }

        private AgentConfig BuildAgentConfig()
        {
            string gpuModel;
            int vramMb;
            int gpuCount;
            bool hasGpu = DetectHasGpu(out gpuModel, out vramMb, out gpuCount);

            return new AgentConfig
            {
                AgentId = _agentId,
                CpuLogicalCores = Environment.ProcessorCount,
                RamTotalMB = GetTotalRamMb(),
                HasGpu = hasGpu,
                GpuModel = gpuModel,
                GpuMemoryMB = vramMb,
                GpuCount = gpuCount,
                OsVersion = Environment.OSVersion.VersionString,
                InstanceId = Guid.NewGuid().ToString(),
                Mac = GetPrimaryMac(),
                Ip = GetLocalIp()
            };
        }

        private static float GetTotalRamMb()
        {
            try
            {
                var gc = GC.GetGCMemoryInfo();
                long bytes = Math.Max(4_000_000_000L, (long)gc.TotalAvailableMemoryBytes);
                return (float)(bytes / (1024.0 * 1024.0));
            }
            catch
            {
                return 16000f;
            }
        }

        private static bool DetectHasGpu(out string model, out int vramMb, out int count)
        {
            // Stub: keep as-is until your GPU probe is ready
            model = "Unknown";
            vramMb = 1024;
            count = 1;
            return true;
        }

        private static string GetPrimaryMac()
        {
            try
            {
                var ni = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up)
                    .OrderByDescending(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                    .ThenByDescending(n => n.Speed)
                    .FirstOrDefault();

                var mac = ni != null ? ni.GetPhysicalAddress() : null;
                return mac != null ? mac.ToString() : "";
            }
            catch
            {
                return "";
            }
        }

        private static string GetLocalIp()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    var props = ni.GetIPProperties();
                    var v4 = props.UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                                             && !IPAddress.IsLoopback(a.Address));
                    if (v4 != null) return v4.Address.ToString();
                }
            }
            catch { }
            return "";
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace('\n', ' ').Replace('\r', ' ');
        }

        // --------- tiny file logger (agent-side only) ----------
        private static class FileLogger
        {
            private static readonly object Gate = new object();
            private static readonly string Dir =
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NodeLinkUI");
            private static readonly string PathLog = System.IO.Path.Combine(Dir, "agent.log");
            private const long MaxBytes = 512 * 1024; // 512KB simple rollover

            private static void Write(string level, string message, Exception ex = null)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                    if (ex != null) line += $" :: {ex.GetType().Name}: {ex.Message}";

                    lock (Gate)
                    {
                        RolloverIfNeeded();
                        File.AppendAllText(PathLog, line + Environment.NewLine, Encoding.UTF8);
                    }
                }
                catch
                {
                    // logging must not crash the agent
                }
            }

            private static void RolloverIfNeeded()
            {
                try
                {
                    var fi = new FileInfo(PathLog);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        string bak = PathLog + ".1";
                        if (File.Exists(bak)) File.Delete(bak);
                        File.Move(PathLog, bak);
                    }
                }
                catch { }
            }

            public static void Info(string msg) => Write("INFO", msg);
            public static void Warn(string msg, Exception ex = null) => Write("WARN", msg, ex);
            public static void Error(string msg, Exception ex = null) => Write("ERROR", msg, ex);
        }
    }
}




