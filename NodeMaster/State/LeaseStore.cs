using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;

namespace NodeMaster.State
{
    public sealed class LeaseStore
    {
        private readonly string _path;
        private readonly TimeSpan _stickyDuration;
        private readonly object _lock = new();
        private Dictionary<string, StickyLease> _byMac = new(StringComparer.OrdinalIgnoreCase);

        public LeaseStore(string path, TimeSpan stickyDuration)
        {
            _path = path;
            _stickyDuration = stickyDuration;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            Load();
        }

        public static string NormalizeMac(string mac)
            => string.IsNullOrWhiteSpace(mac) ? "" : mac.Replace(":", "").Replace("-", "").ToUpperInvariant();

        public StickyLease? TryGetByMac(string mac)
        {
            var key = NormalizeMac(mac);
            lock (_lock)
            {
                if (_byMac.TryGetValue(key, out var l) && l.ExpiresUtc > DateTime.UtcNow)
                    return l;
                return null;
            }
        }

        public StickyLease Upsert(string mac, IPAddress ip, string? agentId = null)
        {
            var key = NormalizeMac(mac);
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                if (_byMac.TryGetValue(key, out var l))
                {
                    l.Ip = ip.ToString();
                    if (!string.IsNullOrWhiteSpace(agentId)) l.AgentId = agentId;
                    l.LastSeenUtc = now;
                    l.ExpiresUtc = now + _stickyDuration;
                    Save();
                    return l;
                }

                var nl = new StickyLease
                {
                    Mac = key,
                    Ip = ip.ToString(),
                    AgentId = agentId,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                    ExpiresUtc = now + _stickyDuration
                };
                _byMac[key] = nl;
                Save();
                return nl;
            }
        }

        public void Touch(string mac)
        {
            var key = NormalizeMac(mac);
            lock (_lock)
            {
                if (_byMac.TryGetValue(key, out var l))
                {
                    l.LastSeenUtc = DateTime.UtcNow;
                    Save();
                }
            }
        }

        public void ReclaimExpired()
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var rm = _byMac.Keys.Where(k => _byMac[k].ExpiresUtc <= now).ToList();
                foreach (var k in rm) _byMac.Remove(k);
                if (rm.Count > 0) Save();
            }
        }

        private void Load()
        {
            lock (_lock)
            {
                if (!File.Exists(_path)) { _byMac = new(); return; }
                var json = File.ReadAllText(_path);
                var list = JsonSerializer.Deserialize<List<StickyLease>>(json) ?? [];
                _byMac = list.ToDictionary(x => x.Mac, x => x, StringComparer.OrdinalIgnoreCase);

                var now = DateTime.UtcNow;
                foreach (var k in _byMac.Keys.ToList())
                    if (_byMac[k].ExpiresUtc <= now) _byMac.Remove(k);
            }
        }

        private void Save()
        {
            lock (_lock)
            {
                var list = _byMac.Values.OrderBy(v => v.Mac).ToList();
                var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_path, json);
            }
        }
    }

    public sealed class StickyLease
    {
        [JsonPropertyName("mac")] public string Mac { get; set; } = "";
        [JsonPropertyName("ip")] public string Ip { get; set; } = "";
        [JsonPropertyName("agentId")] public string? AgentId { get; set; }

        [JsonPropertyName("firstSeenUtc")] public DateTime FirstSeenUtc { get; set; }
        [JsonPropertyName("lastSeenUtc")] public DateTime LastSeenUtc { get; set; }
        [JsonPropertyName("expiresUtc")] public DateTime ExpiresUtc { get; set; }
    }
}

