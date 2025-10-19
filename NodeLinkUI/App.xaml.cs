using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;

using NodeCore.Config; // NodeLinkConfig.Load()

namespace NodeLinkUI
{
    public partial class App : Application
    {
        private async void Application_Startup(object sender, StartupEventArgs e)
        {
            // Show splash
            var splash = new SplashWindow();
            splash.Show();

            // Keep startup snappy
            await Task.Delay(600);

            // Load config (optional: useful for early diagnostics)
            var cfg = NodeLinkConfig.Load();
            Debug.WriteLine($"[UI] CFG Http={cfg.UseHttpControlPlane} UdpReg={cfg.UseLegacyUdpRegistration} HealthSecs={cfg.HealthPollSeconds}");

            // Create and show main window (all master/agent bootstrap now lives in MainWindow)
            var main = new MainWindow();
            MainWindow = main;
            main.Show();

            splash.Close();
        }

        // Helper kept in case you use it elsewhere later
        private static string GetPrimaryIPv4()
        {
            try
            {
                string best = "127.0.0.1";
                foreach (var ni in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
                {
                    if (ni.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ni))
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




