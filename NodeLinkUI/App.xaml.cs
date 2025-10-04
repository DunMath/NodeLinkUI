using System.Configuration;
using System.Data;
using System.Threading.Tasks;
using System.Windows;

namespace NodeLinkUI
{
    public partial class App : Application
    {
        private async void Application_Startup(object sender, StartupEventArgs e)
        {
            var splash = new SplashWindow();
            splash.Show();

            // Keep this short/snappy
            await Task.Delay(1200);

            var main = new MainWindow();
            MainWindow = main;
            main.Show();

            splash.Close();
        }
    }
}

