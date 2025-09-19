using System;
using System.Windows;
using NodeCore; // for NodeLinkSettings

namespace NodeLinkUI
{
    public partial class SettingsWindow : Window
    {
        private NodeLinkSettings settings;

        public SettingsWindow(NodeLinkSettings currentSettings)
        {
            InitializeComponent();
            settings = currentSettings;
            DataContext = settings;   // binding handles UI <-> settings
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                settings.Save();   // values already updated via binding
                MessageBox.Show("Settings saved successfully.");
                this.DialogResult = true;
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save settings: {ex.Message}");
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}

