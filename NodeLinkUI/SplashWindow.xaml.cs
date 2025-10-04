using System.Reflection;
using System.Windows;

namespace NodeLinkUI
{
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();

            // Show app version (optional)
            var asm = Assembly.GetExecutingAssembly().GetName();
            VersionText.Text = $"v{asm.Version?.ToString() ?? "1.0.0"}";
        }
    }
}
