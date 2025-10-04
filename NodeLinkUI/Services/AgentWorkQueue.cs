// File: NodeLinkUI/Services/AgentWorkQueue.cs
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace NodeLinkUI.Services
{
    public sealed class AgentWorkQueue : IDisposable
    {
        private readonly ConcurrentQueue<string> _queue = new();
        private readonly SemaphoreSlim _gate = new(0);
        private readonly Func<string, Task> _processAsync;
        private readonly Action<string> _log;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _runner;

        public AgentWorkQueue(Func<string, Task> processAsync, Action<string> log)
        {
            _processAsync = processAsync ?? throw new ArgumentNullException(nameof(processAsync));
            _log = log ?? (_ => { });
            _runner = Task.Run(RunAsync);
        }

        public void Enqueue(string payload)
        {
            _queue.Enqueue(payload);
            _gate.Release();
        }

        private async Task RunAsync()
        {
            var ct = _cts.Token;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await _gate.WaitAsync(ct);
                    if (_queue.TryDequeue(out var payload))
                    {
                        try { await _processAsync(payload); }
                        catch (Exception ex) { _log($"AgentWorkQueue error: {ex.Message}"); }
                    }
                }
            }
            catch (OperationCanceledException) { /* normal */ }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _runner.Wait(200); } catch { /* ignore */ }
            _gate.Dispose();
            _cts.Dispose();
        }
    }
}

