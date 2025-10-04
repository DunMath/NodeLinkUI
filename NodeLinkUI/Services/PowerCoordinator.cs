using Microsoft.Win32;
using NodeComm;
using NodeCore;
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace NodeLinkUI.Services
{
    /// <summary>
    /// Pro-only power features. In Free tier, UI is disabled and logic is ignored.
    /// </summary>
    internal sealed class PowerCoordinator
    {
        private readonly NodeLinkSettings _settings;
        private readonly CommChannel _comm;
        private readonly Func<string> _getAgentId;
        private readonly Action<string> _log;

        // Pending intent (Agent)
        private DateTime? _pendingDeadlineUtc;
        private string? _pendingMode;
        private DateTime _lastActiveUtc = DateTime.UtcNow;
        private DateTime _masterLeaseExpiryUtc = DateTime.UtcNow.AddMinutes(1);

        public PowerCoordinator(NodeLinkSettings settings, CommChannel comm, Func<string> getAgentId, Action<string> log)
        {
            _settings = settings;
            _comm = comm;
            _getAgentId = getAgentId;
            _log = log;
        }

        public void WireSystemEvents(Window owner, bool isAgent)
        {
            if (!isAgent) return;
            SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
            owner.Closing += (_, __) => SendAgentLeaving("AppClosing");
        }

        public void ApplyStartupPolicy(bool isAgent)
        {
            if (!isAgent) return;
            // Free tier: ignore all power automation
            if (!_settings.ProMode) { ReleaseKeepAwake(); return; }

            // If Pro & AlwaysOn is enabled, keep system awake while UI runs
            if (_settings.AlwaysOnWhileRunning)
                KeepSystemAwake(enable: true, useAwayMode: true);
        }

        public void OnAgentHeartbeat(bool busy)
        {
            if (!_settings.ProMode) { ReleaseKeepAwake(); return; }

            // Keep awake if AlwaysOn or busy with tasks
            KeepSystemAwake(_settings.AlwaysOnWhileRunning || busy, useAwayMode: true);

            // Mark activity
            if (busy) _lastActiveUtc = DateTime.UtcNow;
        }

        public void CheckPowerAction()
        {
            if (!_settings.ProMode) return;

            bool idleLongEnough = (DateTime.UtcNow - _lastActiveUtc) >= TimeSpan.FromMinutes(_settings.AgentIdleMinutesToAct);

            // Case 1: explicit Master intent
            if (_pendingDeadlineUtc.HasValue && DateTime.UtcNow >= _pendingDeadlineUtc.Value)
            {
                if (idleLongEnough) PerformPowerAction(_pendingMode ?? "ExitApp");
                else Ack($"AgentAck:PowerIntent:{_getAgentId()}:Deferred(Busy)");
                return;
            }

            // Case 2: lost Master (lease expired + buffer)
            if (DateTime.UtcNow >= _masterLeaseExpiryUtc.AddMinutes(_settings.LostMasterWaitMinutes) && idleLongEnough)
            {
                // No automatic action by default; leave as comment if you later want auto-exit
                // PerformPowerAction("ExitApp");
            }
        }

        /// <summary>Agent-side: returns true if handled.</summary>
        public bool TryHandleMessage(string message, bool isAgent)
        {
            if (!isAgent) return false;

            if (message.StartsWith("MasterPowerIntent:", StringComparison.Ordinal))
            {
                if (!_settings.ProMode || !_settings.FollowMasterPower)
                {
                    Ack($"AgentAck:PowerIntent:{_getAgentId()}:Deferred(ProFeature)");
                    _log("Ignored MasterPowerIntent (Pro feature).");
                    return true;
                }

                var dict = message.Substring("MasterPowerIntent:".Length)
                                  .Split(';')
                                  .Select(p => p.Split('='))
                                  .Where(a => a.Length == 2)
                                  .ToDictionary(a => a[0], a => a[1], StringComparer.OrdinalIgnoreCase);

                _pendingMode = dict.TryGetValue("Mode", out var m) ? m : "ExitApp";
                int grace = dict.TryGetValue("GraceSec", out var g) && int.TryParse(g, out var gi) ? gi : 60;
                long issued = dict.TryGetValue("IssuedUtc", out var iu) && long.TryParse(iu, out var it) ? it : DateTime.UtcNow.Ticks;
                _pendingDeadlineUtc = new DateTime(issued, DateTimeKind.Utc).AddSeconds(grace);

                Ack($"AgentAck:PowerIntent:{_getAgentId()}:WillComply");
                _log($"Received MasterPowerIntent: mode={_pendingMode}, grace={grace}s.");
                return true;
            }
            else if (message.StartsWith("MasterLease:", StringComparison.Ordinal))
            {
                var part = message.Substring("MasterLease:".Length).Split('=');
                if (part.Length == 2 && long.TryParse(part[1], out var ticks))
                    _masterLeaseExpiryUtc = new DateTime(ticks, DateTimeKind.Utc);
                return true;
            }

            return false;
        }
        private void SystemEvents_PowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            // Optional: just log; no special handling required for now
            _log($"System power mode changed: {e.Mode}");
        }

        // ---------- helpers ----------

        private void Ack(string msg)
        {
            try { _comm.SendToMaster(msg); } catch { /* ignore */ }
        }

        private void SendAgentLeaving(string reason)
        {
            try { _comm.SendToMaster($"AgentLeaving:{_getAgentId()}:{reason}"); } catch { /* ignore */ }
        }

        // keep-awake
        [Flags]
        private enum EXECUTION_STATE : uint
        {
            ES_AWAYMODE_REQUIRED = 0x00000040,
            ES_CONTINUOUS = 0x80000000,
            ES_SYSTEM_REQUIRED = 0x00000001
        }

        [DllImport("kernel32.dll")]
        private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

        private bool _keepAwakeActive;

        private void KeepSystemAwake(bool enable, bool useAwayMode)
        {
            if (enable && !_keepAwakeActive)
            {
                var flags = EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED;
                if (useAwayMode) flags |= EXECUTION_STATE.ES_AWAYMODE_REQUIRED;
                SetThreadExecutionState(flags);
                _keepAwakeActive = true;
                _log("Keep-awake enabled.");
            }
            else if (!enable && _keepAwakeActive)
            {
                SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
                _keepAwakeActive = false;
                _log("Keep-awake released.");
            }
        }

        private void ReleaseKeepAwake()
        {
            if (_keepAwakeActive)
            {
                try { SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS); } catch { }
                _keepAwakeActive = false;
            }
        }

        // power actions
        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        private void PerformPowerAction(string mode)
        {
            try
            {
                SendAgentLeaving($"PowerAction:{mode}");
                _log($"Performing power action: {mode}");

                switch (mode)
                {
                    case "ExitApp":
                        Application.Current.Shutdown();
                        break;
                    case "Sleep":
                        SetSuspendState(false, true, false);
                        break;
                    case "Hibernate":
                        SetSuspendState(true, true, false);
                        break;
                    case "Shutdown":
                        Process.Start(new ProcessStartInfo("shutdown", "/s /t 0")
                        { UseShellExecute = false, CreateNoWindow = true });
                        break;
                }
            }
            catch (Exception ex)
            {
                _log($"Power action failed: {ex.Message}");
            }
        }
    }
}

