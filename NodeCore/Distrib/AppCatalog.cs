// NodeCore/Distrib/AppCatalog.cs
using System;
using System.Collections.Generic;
using System.Reflection;

namespace NodeCore.Distrib
{
    public static class AppCatalog
    {
        private static readonly Dictionary<string, IDistributedApp> _apps =
            new(StringComparer.OrdinalIgnoreCase);

        public static void Register(IDistributedApp app) => _apps[app.Id] = app;
        public static IDistributedApp? Get(string id) => _apps.TryGetValue(id, out var a) ? a : null;

        public static void AutoDiscover(params Assembly[] assemblies)
        {
            foreach (var asm in assemblies)
            {
                foreach (var t in asm.GetTypes())
                {
                    if (t.IsAbstract) continue;
                    if (typeof(IDistributedApp).IsAssignableFrom(t))
                    {
                        if (Activator.CreateInstance(t) is IDistributedApp app) Register(app);
                    }
                }
            }
        }

        public static IReadOnlyCollection<string> ListIds() => _apps.Keys;
    }
}

