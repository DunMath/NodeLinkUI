using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NodeCore
{
    public class NodeLinkSettings
    {
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
        /// List of nominated apps available for dispatch.
        /// </summary>
        public List<NominatedApp> NominatedApps { get; set; } = new();

        /// <summary>
        /// Save settings to a JSON file.
        /// </summary>
        public void Save(string path = "settings.json")
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        /// <summary>
        /// Load settings from a JSON file, or return defaults if not found.
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
                catch (Exception)
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
        public string Name { get; set; }

        /// <summary>
        /// Path to the executable or launch command.
        /// </summary>
        public string Path { get; set; }
    }
}

