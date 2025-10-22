// NodeCore/Exec/LocalExecutor.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NodeCore.Exec
{
    using global::NodeCore.Orchestration;
    using NodeCore;

    /// <summary>
    /// Minimal local executor: bounded queue + N workers.
    /// Emits local queue-wait and "local RTT" (queue+exec) metrics.
    /// NOTE: This is headless (no UI). It does not alter your remote pipeline.
    /// </summary>
    public sealed class LocalExecutor : IAsyncDisposable
    {
        private readonly Channel<LocalJob> _queue;
        private readonly List<Task> _workers = new();
        private readonly CancellationTokenSource _cts = new();

        private int _inFlight;

        // Simple EWMAs for local exec/queue (if you want to read them later)
        private double _ewmaExecMs;
        private double _ewmaQueueMs;
        private const double Alpha = 0.25; // smoothing

        public int Concurrency { get; }
        public int QueueLength => _queue.Reader.Count;
        public int InFlight => Volatile.Read(ref _inFlight);
        public double AvgExecMs => _ewmaExecMs;
        public double AvgQueueMs => _ewmaQueueMs;

        /// <summary>
        /// Optional delegate to actually execute a TaskUnit locally.
        /// If null, we just yield (metrics still work).
        /// </summary>
        private readonly Func<TaskUnit, Task>? _runner;

        /// <summary>
        /// MetricsMeasured(agentId, app, seq, queueWaitMs, localRttMs)
        /// We’ll publish agentId="Master" so the UI can reuse the Master row.
        /// </summary>
        public event Action<string, string, int, double, double>? MetricsMeasured;

        public LocalExecutor(
            int? preferredConcurrency = null,
            int boundedCapacity = 1024,
            Func<TaskUnit, Task>? runner = null)
        {
            Concurrency = Math.Max(1, preferredConcurrency ?? Math.Min(Environment.ProcessorCount / 2, 4));
            _runner = runner;

            _queue = Channel.CreateBounded<LocalJob>(new BoundedChannelOptions(boundedCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait
            });

            for (int i = 0; i < Concurrency; i++)
                _workers.Add(Task.Run(WorkerLoop));
        }

        public void Enqueue(TaskUnit unit)
        {
            var job = new LocalJob
            {
                App = unit.App ?? "App",
                Seq = unit.Seq,
                Payload = unit.Payload ?? string.Empty,
                EnqueueUtc = DateTimeOffset.UtcNow
            };
            _queue.Writer.TryWrite(job);
        }

        public void EnqueueBatch(IEnumerable<TaskUnit> units)
        {
            foreach (var u in units) Enqueue(u);
        }

        private async Task WorkerLoop()
        {
            var ct = _cts.Token;
            while (!ct.IsCancellationRequested)
            {
                LocalJob job;
                try
                {
                    job = await _queue.Reader.ReadAsync(ct);
                }
                catch (OperationCanceledException) { break; }

                var start = DateTimeOffset.UtcNow;
                var qms = (start - job.EnqueueUtc).TotalMilliseconds;

                Interlocked.Increment(ref _inFlight);
                try
                {
                    if (_runner != null)
                    {
                        // Call the host-provided local runner
                        await _runner(new TaskUnit
                        {
                            App = job.App,
                            Seq = job.Seq,
                            Payload = job.Payload
                        });
                    }
                    else
                    {
                        // Default: minimal yield so metrics still update
                        await Task.Yield();
                    }
                }
                catch
                {
                    // Swallow for metrics. You can add error events later if needed.
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }

                var end = DateTimeOffset.UtcNow;
                var execMs = (end - start).TotalMilliseconds;
                var rttMs = qms + execMs;

                // Update EWMAs
                _ewmaExecMs = _ewmaExecMs == 0 ? execMs : (1 - Alpha) * _ewmaExecMs + Alpha * execMs;
                _ewmaQueueMs = _ewmaQueueMs == 0 ? qms : (1 - Alpha) * _ewmaQueueMs + Alpha * qms;

                // Emit metrics into the Master row
                MetricsMeasured?.Invoke("Master", job.App, job.Seq, qms, rttMs);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _queue.Writer.TryComplete(); } catch { }
            try { await Task.WhenAll(_workers); } catch { }
            _cts.Dispose();
        }

        private sealed class LocalJob
        {
            public string App = "App";
            public int Seq;
            public string Payload = "";
            public DateTimeOffset EnqueueUtc;
        }
    }
}


