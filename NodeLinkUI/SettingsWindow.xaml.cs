using Microsoft.Win32;
using NodeCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace NodeLinkUI
{
    [ValueConversion(typeof(bool), typeof(Visibility))]
    public class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return (value is bool && (bool)value) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return value is Visibility && (Visibility)value == Visibility.Visible;
        }
    }

    public partial class SettingsWindow : Window
    {
        private NodeLinkSettings settings;
        private List<AgentStatus> agentStatuses;
        private bool isProVersion;
        private List<NominatedApp> tempApps;

        public SettingsWindow(NodeLinkSettings settings, List<AgentStatus> agentStatuses, bool isProVersion)
        {
            InitializeComponent();
            this.settings = settings;
            this.agentStatuses = agentStatuses;
            this.isProVersion = isProVersion;
            tempApps = new List<NominatedApp>(settings.NominatedApps);
            MasterIpTextBox.Text = settings.MasterIp;
            HeartbeatIntervalTextBox.Text = settings.HeartbeatIntervalMs.ToString();
            UseGpuCheckBox.IsChecked = settings.UseGpuGlobally;
            SelectedAppsListBox.ItemsSource = tempApps;
            SelectedAppsListBox.DisplayMemberPath = "Name";
            // Comment out PreferredAgentComboBox population to avoid CS1061
            // if (isProVersion)
            // {
            //     PreferredAgentComboBox.ItemsSource = agentStatuses.Select(a => a.AgentId);
            // }
        }

        private void AddAppButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
                Title = "Select Application"
            };

            if (dialog.ShowDialog() == true)
            {
                var app = new NominatedApp
                {
                    Name = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName),
                    Path = dialog.FileName
                };
                tempApps.Add(app);
                SelectedAppsListBox.ItemsSource = null;
                SelectedAppsListBox.ItemsSource = tempApps;
                SelectedAppsListBox.DisplayMemberPath = "Name";
            }
        }

        private void RemoveAppButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedAppsListBox.SelectedItem is NominatedApp app)
            {
                tempApps.Remove(app);
                SelectedAppsListBox.ItemsSource = null;
                SelectedAppsListBox.ItemsSource = tempApps;
                SelectedAppsListBox.DisplayMemberPath = "Name";
            }
        }

        private void SelectedAppsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SelectedAppsListBox.SelectedItem is NominatedApp app)
            {
                AppNameTextBox.Text = app.Name;
                AppPathTextBox.Text = app.Path;
                // Comment out PreferredAgent to avoid CS1061
                // PreferredAgentComboBox.SelectedItem = app.PreferredAgent;
            }
            else
            {
                AppNameTextBox.Text = string.Empty;
                AppPathTextBox.Text = string.Empty;
                // PreferredAgentComboBox.SelectedItem = null;
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(HeartbeatIntervalTextBox.Text, out int interval) || interval < 1000)
            {
                MessageBox.Show("Heartbeat interval must be a number >= 1000 ms.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            settings.MasterIp = MasterIpTextBox.Text;
            settings.HeartbeatIntervalMs = interval;
            settings.UseGpuGlobally = UseGpuCheckBox.IsChecked ?? false;
            settings.NominatedApps = new List<NominatedApp>(tempApps);
            // Comment out PreferredAgent to avoid CS1061
            // foreach (var app in settings.NominatedApps)
            // {
            //     app.PreferredAgent = isProVersion ? PreferredAgentComboBox.SelectedItem?.ToString() : null;
            // }
            settings.Save();
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}

