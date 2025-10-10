// File: NodeLinkUI/MainWindow.SoftThreads.cs
// NodeLinkUI/MainWindow.SoftThreads.cs — v1 functional SoftThreads
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NodeLinkUI
{
    public partial class MainWindow
    {
        // -------- AGENT SIDE: a small single-consumer work queue --------
        private sealed class AgentWorkQueue : IDisposable
        {
            private readonly MainWindow _owner;
            private readonly ConcurrentQueue<string> _q = new();
            private readonly CancellationTokenSource _cts = new();
            private readonly Task _worker;

            public int Count => _q.Count;

            public AgentWorkQueue(MainWindow owner)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                _worker = Task.Run(WorkerLoopAsync);
            }

            public void Enqueue(string message)
            {
                if (string.IsNullOrWhiteSpace(message)) return;
                _q.Enqueue(message);
            }

            private async Task WorkerLoopAsync()
            {
                var ct = _cts.Token;
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        if (!_q.TryDequeue(out var msg))
                        {
                            await Task.Delay(50, ct);
                            continue;
                        }

                        await HandleMessageAsync(msg, ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch
                    {
                        // keep going
                    }
                }
            }

            private async Task HandleMessageAsync(string msg, CancellationToken ct)
            {
                // Very small “work” simulation so we can test end-to-end
                if (msg.StartsWith("Compute:", StringComparison.OrdinalIgnoreCase))
                {
                    // Format: Compute:{app}|{seq}|{payload}
                    var payload = msg.Substring("Compute:".Length);
                    string app = "App";
                    int seq = 0;

                    try
                    {
                        var parts = payload.Split('|');
                        if (parts.Length >= 2)
                        {
                            app = parts[0];
                            _ = int.TryParse(parts[1], out seq);
                        }
                    }
                    catch { /* best-effort parse */ }

                    await Task.Delay(150, ct); // simulate work

                    // Report both a simple Result and a ComputeResult (so either handler can pick it up)
                    _owner.comm.SendToMaster($"Result:OK:{app}|{seq}");
                    _owner.comm.SendToMaster($"ComputeResult:{app}|{seq}:OK");
                    _owner.Log($"Agent executed {app} seq {seq}");
                    return;
                }

                if (msg.StartsWith("CustomTask:", StringComparison.OrdinalIgnoreCase))
                {
                    // Format: CustomTask:{taskId}:{text}
                    await Task.Delay(120, ct);
                    _owner.comm.SendToMaster($"Result:CustomTaskDone:{msg}");
                    _owner.Log("Agent completed custom task.");
                    return;
                }

                // Unknown → just acknowledge so upstream doesn’t stall
                await Task.Delay(30, ct);
                _owner.comm.SendToMaster($"Result:Acknowledged:{msg}");
            }

            public void Dispose()
            {
                try { _cts.Cancel(); } catch { }
                try { _worker.Wait(500); } catch { }
                _cts.Dispose();
            }
        }

        // -------- MASTER SIDE: a simple dispatcher with cached/in-flight counts --------
        private sealed class SoftThreadDispatcher : IDisposable
        {
            private readonly MainWindow _owner;
            private readonly ConcurrentQueue<(string agentId, string taskId, string payload)> _cache = new();
            private int _inflight;
            private readonly CancellationTokenSource _cts = new();
            private readonly Task _pump;

            public int InFlightCount => _inflight;
            public int CachedCount => _cache.Count;

            public SoftThreadDispatcher(MainWindow owner)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                _pump = Task.Run(PumpLoopAsync);
            }

            public void Enqueue(string agentId, string taskId, string payload)
            {
                if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(payload)) return;
                _cache.Enqueue((agentId, taskId, payload));
            }

            private async Task PumpLoopAsync()
            {
                var ct = _cts.Token;
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        if (!_cache.TryDequeue(out var item))
                        {
                            await Task.Delay(40, ct);
                            continue;
                        }

                        Interlocked.Increment(ref _inflight);

                        // Try NodeMaster first, fall back to direct comms
                        bool sent = false;
                        try
                        {
                            if (_owner.master != null)
                            {
                                _owner.master.DispatchTaskToAgent(item.payload, item.agentId);
                                sent = true;
                            }
                            else
                            {
                                sent = _owner.comm.SendToAgent(item.agentId, item.payload);
                            }
                        }
                        catch { sent = false; }

                        if (!sent)
                        {
                            // Couldn’t send: requeue and backoff
                            _cache.Enqueue(item);
                            await Task.Delay(200, ct);
                            Interlocked.Decrement(ref _inflight);
                            continue;
                        }

                        // Small pacing to avoid blasting a single agent
                        await Task.Delay(10, ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch
                    {
                        // keep pumping
                    }
                    finally
                    {
                        // InFlight is decremented by OnResultReceived; as a safety guard,
                        // ensure it never goes negative.
                        if (_inflight < 0) Interlocked.Exchange(ref _inflight, 0);
                    }
                }
            }

            public void OnResultReceived(string agentId)
            {
                // For now we assume one result per enqueued work item.
                if (Interlocked.Decrement(ref _inflight) < 0)
                    Interlocked.Exchange(ref _inflight, 0);
            }

            public void Dispose()
            {
                try { _cts.Cancel(); } catch { }
                try { _pump.Wait(500); } catch { }
                _cts.Dispose();
            }
        }

        // -------- Wiring into MainWindow --------

        private SoftThreadDispatcher? _softDispatcher;
        private AgentWorkQueue? _agentWork;

        // Called during role initialization in MainWindow.xaml.cs
        private void InitializeSoftThreadsForMaster()
        {
            _softDispatcher = new SoftThreadDispatcher(this);
        }

        private void InitializeSoftThreadsForAgent()
        {
            _agentWork = new AgentWorkQueue(this);
        }

        private void DisposeSoftThreads()
        {
            try { _softDispatcher?.Dispose(); } catch { }
            try { _agentWork?.Dispose(); } catch { }
            _softDispatcher = null;
            _agentWork = null;
        }

        // Optional: update any UI counters tied to soft threads (safe no-op if unbound)
        private void UpdateSoftThreadStats()
        {
            // If you later add columns for in-flight/cached, you can propagate counts here.
            // Example:
            // var me = agentStatuses.FirstOrDefault(a => a.AgentId == "Master");
            // if (me != null && _softDispatcher != null) { ... }
        }
    }
}

