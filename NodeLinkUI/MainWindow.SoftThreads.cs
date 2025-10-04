// File: NodeLinkUI/MainWindow.SoftThreads.cs
using System;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Threading;
using NodeCore;
using NodeLinkUI.Services;

namespace NodeLinkUI
{
    public partial class MainWindow
    {
        // Only this field is declared here (the others likely exist in another partial).
        private DispatcherTimer _softUiTimer;

        /// <summary>
        /// Call from your Master role initialization (after agentStatuses are bound).
        /// </summary>
        private void InitializeSoftThreadsForMaster()
        {
            // Ensure default exists even if settings.json was older
            if (settings.MaxSoftThreadsPerCore <= 0)
                settings.MaxSoftThreadsPerCore = 3;

            _softDispatcher ??= new SoftThreadDispatcher(
                sendFunc: (payload, agentId) =>
                {
                    master?.DispatchTaskToAgent(payload, agentId);
                    return true;
                },
                getStatuses: () => agentStatuses,
                settings: settings,
                log: Log,
                flushIntervalMs: 60
            );

            // Light UI refresher (optional; harmless if summary controls don't exist)
            _softUiTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _softUiTimer.Tick -= SoftUiTimer_Tick;
            _softUiTimer.Tick += SoftUiTimer_Tick;
            _softUiTimer.Start();
        }

        /// <summary>
        /// Call from your Agent role initialization (after agentStatuses are bound).
        /// </summary>
        private void InitializeSoftThreadsForAgent()
        {
            _agentWork ??= new AgentWorkQueue(
                async (payload) =>
                {
                    // TODO: Replace with actual execution of 'payload'
                    await System.Threading.Tasks.Task.Delay(50);
                    comm.SendToMaster($"Result:{payload}:OK");
                },
                Log
            );
        }

        private void SoftUiTimer_Tick(object sender, EventArgs e) => UpdateSoftThreadStats();

        /// <summary>
        /// Safe UI updater for Soft Threads summary; ok if controls are not present.
        /// Also pushes per-agent "cached" counts into AgentStatus.SoftThreadsQueued.
        /// </summary>
        private void UpdateSoftThreadStats()
        {
            if (currentRole != NodeRole.Master) return;

            try
            {
                int capacity = 0;
                int agentQueues = 0;

                foreach (var a in agentStatuses)
                {
                    if (string.Equals(a.AgentId, "Master", StringComparison.OrdinalIgnoreCase))
                        continue;

                    int cores = a.CpuLogicalCores > 0 ? a.CpuLogicalCores : 4;
                    capacity += Math.Max(1, settings.MaxSoftThreadsPerCore * cores);
                    agentQueues += Math.Max(0, a.TaskQueueLength);
                }

                int queued = 0, pendingLocal = 0;

                // Fill per-agent cached counts (queued+pending) from dispatcher
                var per = _softDispatcher?.SnapshotPerAgent();
                if (_softDispatcher != null)
                {
                    var totals = _softDispatcher.GetTotals();
                    queued = totals.Queued;
                    pendingLocal = totals.PendingLocal;

                    if (per != null)
                    {
                        foreach (var a in agentStatuses)
                        {
                            if (string.Equals(a.AgentId, "Master", StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (per.TryGetValue(a.AgentId, out var v))
                                a.SoftThreadsQueued = Math.Max(0, v.Queued + v.Pending);
                            else
                                a.SoftThreadsQueued = 0;
                        }

                        // Refresh the grid so the new column updates
                        Dispatcher.Invoke(() =>
                        {
                            if (NodeGrid != null)
                            {
                                NodeGrid.ItemsSource = null;
                                NodeGrid.ItemsSource = agentStatuses;
                            }
                        });
                    }
                }

                int active = agentQueues + pendingLocal;

                // Update summary text if present
                if (TryFind<TextBlock>("SoftThreadSummaryText") is TextBlock summary)
                    summary.Text = $"Soft Threads: Active {active}/{capacity} • Queued {queued}";

                // Update capacity bar if present
                if (TryFind<ProgressBar>("SoftThreadCapacityBar") is ProgressBar bar)
                {
                    bar.Maximum = Math.Max(1, capacity);
                    bar.Value = Math.Min(active, capacity);
                }
            }
            catch
            {
                // swallow timing/race UI errors
            }
        }

        /// <summary>
        /// Call on app exit/restart to clean up timers/queues.
        /// </summary>
        private void DisposeSoftThreads()
        {
            try
            {
                if (_softUiTimer != null)
                {
                    _softUiTimer.Stop();
                    _softUiTimer.Tick -= SoftUiTimer_Tick;
                    _softUiTimer = null;
                }

                _agentWork?.Dispose();
                _agentWork = null;

                _softDispatcher?.Dispose();
                _softDispatcher = null;
            }
            catch
            {
                // no-op
            }
        }

        /// <summary>
        /// Small helper: safe lookup of XAML-named controls without hard dependency.
        /// </summary>
        private T TryFind<T>(string name) where T : class
        {
            return FindName(name) as T;
        }
    }
}



