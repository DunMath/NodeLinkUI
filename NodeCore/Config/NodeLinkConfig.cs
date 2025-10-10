// NodeCore/Config/NodeLinkConfig.cs
using System.Text.Json;

namespace NodeCore.Config;

public sealed record NodeLinkConfig
{
    // ---- Defaults (safe to run without any file/env) ----
    public bool UseHttpControlPlane { get; init; } = true;
    public bool UseLegacyUdpRegistration { get; init; } = false;
    public bool UseLegacyUdpKeepAlive { get; init; } = false;

    public int ControlPort { get; init; } = 50555;
    public string? AuthToken { get; init; } = null; // null = dev/no auth
    public int HealthPollSeconds { get; init; } = 15;

    public string Version { get; init; } = "1.0.1";
    public string MdnsService { get; init; } = "_nodelink._tcp";

    // ---- Loader: defaults < file < env ----
    public static NodeLinkConfig Load(string? appBaseDir = null, string fileName = "NodeLink.config.json")
    {
        var cfg = new NodeLinkConfig();

        // try file in exe folder (Agent, Master, UI can each have their own)
        try
        {
            appBaseDir ??= AppContext.BaseDirectory;
            var path = Path.Combine(appBaseDir, fileName);
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var fromFile = JsonSerializer.Deserialize<NodeLinkConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (fromFile is not null) cfg = cfg.Merge(fromFile);
            }
        }
        catch
        {
            // ignore, keep defaults
        }

        // env overrides (optional)
        cfg = cfg with
        {
            UseHttpControlPlane = GetBool("NL_USE_HTTP", cfg.UseHttpControlPlane),
            UseLegacyUdpRegistration = GetBool("NL_USE_UDP_REG", cfg.UseLegacyUdpRegistration),
            UseLegacyUdpKeepAlive = GetBool("NL_USE_UDP_PING", cfg.UseLegacyUdpKeepAlive),
            ControlPort = GetInt("NL_CONTROL_PORT", cfg.ControlPort),
            AuthToken = GetStr("NL_AUTH_TOKEN", cfg.AuthToken),
            HealthPollSeconds = GetInt("NL_HEALTH_SECS", cfg.HealthPollSeconds),
            Version = GetStr("NL_VERSION", cfg.Version) ?? cfg.Version,
            MdnsService = GetStr("NL_MDNS_SVC", cfg.MdnsService) ?? cfg.MdnsService
        };

        return cfg;
    }

    private NodeLinkConfig Merge(NodeLinkConfig b) => this with
    {
        UseHttpControlPlane = b.UseHttpControlPlane,
        UseLegacyUdpRegistration = b.UseLegacyUdpRegistration,
        UseLegacyUdpKeepAlive = b.UseLegacyUdpKeepAlive,
        ControlPort = b.ControlPort,
        AuthToken = b.AuthToken,
        HealthPollSeconds = b.HealthPollSeconds,
        Version = b.Version,
        MdnsService = b.MdnsService
    };

    private static bool GetBool(string key, bool def) => bool.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : def;
    private static int GetInt(string key, int def) => int.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : def;
    private static string? GetStr(string key, string? def) => Environment.GetEnvironmentVariable(key) ?? def;
}

