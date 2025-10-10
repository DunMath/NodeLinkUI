using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NodeCore
{
    public class NodeLinkSettings
    {
        public bool StartAsMaster { get; set; } = false;
        /// <summary>
        /// IP address of the master node.
        /// </summary>
        public string MasterIp { get; set; } = "192.168.1.100";

        /// <summary>
        /// Heartbeat interval in milliseconds.
        /// </summary>
        public int HeartbeatIntervalMs { get; set; } = 5000;

        /// <summary>
        /// If true, master will prefer agents with GPU for task dispatch.
        /// </summary>
        public bool UseGpuGlobally { get; set; } = true;

        /// <summary>
        /// Pro plan toggle. When false, power features are disabled/ignored and UI is greyed out.
        /// </summary>
        public bool ProMode { get; set; } = false;

        /// <summary>
        /// (Pro) Keep this node awake while NodeLinkUI runs (screen may sleep).
        /// Ignored when ProMode == false.
        /// </summary>
        public bool AlwaysOnWhileRunning { get; set; } = false;

        /// <summary>
        /// (Pro) Agent follows Master power intents (Exit/Sleep/Hibernate/Shutdown).
        /// Ignored when ProMode == false.
        /// </summary>
        public bool FollowMasterPower { get; set; } = true;

        /// <summary>
        /// (Pro) Minutes the Agent must be idle before acting on power intents.
        /// </summary>
        public int AgentIdleMinutesToAct { get; set; } = 5;

        /// <summary>
        /// (Pro) Minutes to wait after Master lease expiry before considering autonomous action.
        /// </summary>
        public int LostMasterWaitMinutes { get; set; } = 10;

        /// <summary>
        /// List of nominated apps available for dispatch.
        /// </summary>
        public List<NominatedApp> NominatedApps { get; set; } = new();

        // ================= SoftThreads settings =================

        /// <summary>
        /// Maximum concurrent soft threads per logical CPU core (Master pacing).
        /// Used to compute per-agent capacity: CpuLogicalCores × MaxSoftThreadsPerCore.
        /// </summary>
        public int MaxSoftThreadsPerCore { get; set; } = 3;

        // ===================== Security (NEW) ====================

        /// <summary>
        /// Human-friendly shared secret (Join Code). If set on Master & Agents,
        /// it’s used to authenticate messages (e.g., HMAC once the channel supports it).
        /// Stored as plain text; you can swap to DPAPI later if needed.
        /// </summary>
        public string JoinCode { get; set; } = "";

        /// <summary>
        /// When true, nodes that don’t present a valid Join Code should be rejected.
        /// You can consult this flag where you enforce admission.
        /// </summary>
        public bool ProhibitUnauthenticated { get; set; } = true;

        /// <summary>
        /// Optional: bind comms to a specific local IP (e.g., your LAN NIC).
        /// Empty = bind to Any. Safe to remove if you don’t want this.
        /// </summary>
        public string BindAddress { get; set; } = "";

        // ========================================================

        /// <summary>
        /// Save settings to a JSON file.
        /// </summary>
        public void Save(string path = "settings.json")
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        /// <summary>
        /// Load settings from a JSON file, or return defaults if not found/corrupt.
        /// Missing properties in older files will use the defaults above.
        /// </summary>
        public static NodeLinkSettings Load(string path = "settings.json")
        {
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<NodeLinkSettings>(json) ?? new NodeLinkSettings();
                }
                catch
                {
                    // If file is corrupt, fall back to defaults
                    return new NodeLinkSettings();
                }
            }
            return new NodeLinkSettings();
        }
    }

    public class NominatedApp
    {
        /// <summary>
        /// Display name of the app.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Path to the executable or launch command.
        /// </summary>
        public string Path { get; set; } = string.Empty;

        // Add PreferredAgent or other fields later if needed (Pro-only).
    }
}


