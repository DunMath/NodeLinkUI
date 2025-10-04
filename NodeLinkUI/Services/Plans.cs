using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using NodeCore;

namespace NodeLinkUI.Services
{
    /// <summary>
    /// Enforces Free-tier agent cap (Master side). Pro = unlimited.
    /// Seats are assigned by first-seen among currently online agents.
    /// </summary>
    internal sealed class Plans
    {
        // Free-tier: 4 Agents (Master excluded) => total visible = 5
        private const int FREE_AGENT_LIMIT = 4;

        private readonly NodeLinkSettings _settings;
        private readonly Func<IEnumerable<AgentStatus>> _getStatuses;
        private readonly Dictionary<string, DateTime> _firstSeen
            = new(StringComparer.OrdinalIgnoreCase);

        public Plans(NodeLinkSettings settings, Func<IEnumerable<AgentStatus>> getStatuses)
        {
            _settings = settings;
            _getStatuses = getStatuses;
        }

        public void TouchFirstSeen(string agentId)
        {
            if (!_firstSeen.ContainsKey(agentId))
                _firstSeen[agentId] = DateTime.UtcNow;
        }

        public bool IsWithinPlan(string agentId)
        {
            if (_settings?.ProMode == true) return true;
            if (string.Equals(agentId, "Master", StringComparison.OrdinalIgnoreCase)) return true;

            var online = _getStatuses()
               .Where(a => a.AgentId != "Master" && a.IsOnline)
               .Select(a => a.AgentId)
               .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var allowed = _firstSeen
               .Where(kv => online.Contains(kv.Key))
               .OrderBy(kv => kv.Value)
               .Take(FREE_AGENT_LIMIT)
               .Select(kv => kv.Key)
               .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return allowed.Contains(agentId);
        }

        public void AddOverCapAlerts(ItemsControl alertBox)
        {
            if (_settings?.ProMode == true) return;

            var statuses = _getStatuses().ToList();
            var online = statuses.Where(a => a.AgentId != "Master" && a.IsOnline).ToList();

            int over = Math.Max(0, online.Count - FREE_AGENT_LIMIT);
            if (over <= 0) return;

            alertBox.Items.Add(
                $"Free plan limit reached: {FREE_AGENT_LIMIT} agents active. {over} agent(s) idle. Upgrade to Pro for unlimited agents.");

            var allowed = _firstSeen
               .Where(kv => online.Any(a => a.AgentId.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)))
               .OrderBy(kv => kv.Value)
               .Take(FREE_AGENT_LIMIT)
               .Select(kv => kv.Key)
               .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var a in online)
            {
                if (!allowed.Contains(a.AgentId))
                    alertBox.Items.Add($"{a.AgentId} is over the Free plan limit and won’t receive tasks. Upgrade to Pro.");
            }
        }
    }
}

