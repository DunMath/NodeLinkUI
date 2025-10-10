using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;

using NodeCore;                  // AgentStatus (typed)
using NodeCore.Config;           // NodeLinkConfig.Load()
using NodeMaster.Bootstrap;      // MasterBootstrapper.Initialize(...)

namespace NodeLinkUI
{
    public partial class App : Application
    {
        private async void Application_Startup(object sender, StartupEventArgs e)
        {
            // --- Splash stays as-is (keeps your current UX) ---
            var splash = new SplashWindow();
            splash.Show();

            // Keep this short/snappy
            await Task.Delay(600);

            // Create the main window first so we can hook logging to the UI later if desired
            var main = new MainWindow();
            MainWindow = main;

            // --- New: load shared config & initialize the master bootstrap once ---
            var cfg = NodeLinkConfig.Load();
            Debug.WriteLine($"[UI] CFG Http={cfg.UseHttpControlPlane} UdpReg={cfg.UseLegacyUdpRegistration} HealthSecs={cfg.HealthPollSeconds}");

            // Typed, minimal delegates (no dynamic / no IQueryable)
            MasterBootstrapper.Initialize(
                log: s => Debug.WriteLine(s),
                getAgents: () => Enumerable.Empty<AgentStatus>(),     // will wire to main's collection later
                updateAgent: a => { /* no-op; UI refresh hook if needed */ },
                getMasterId: () => "Master",
                getMasterIp: () => GetPrimaryIPv4()
            );

            // Show main and close splash
            main.Show();
            splash.Close();
        }

        // Local helper so we don't depend on external NetUtil in this UI layer
        private static string GetPrimaryIPv4()
        {
            try
            {
                string best = "127.0.0.1";
                foreach (var ni in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
                {
                    if (ni.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ni))
                    {
                        best = ni.ToString();
                        break;
                    }
                }
                return best;
            }
            catch
            {
                return "127.0.0.1";
            }
        }
    }
}



