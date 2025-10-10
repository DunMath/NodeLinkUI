using Microsoft.Win32;
using NodeCore;
using NodeLinkUI.Services; // SecurityManager (Join Code helpers)
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NodeLinkUI
{
    public partial class SettingsWindow : Window
    {
        private readonly NodeLinkSettings settings;
        private readonly List<AgentStatus> agentStatuses;
        private readonly bool isProVersion;

        private readonly ObservableCollection<NominatedApp> _apps;

        public SettingsWindow(NodeLinkSettings settings, List<AgentStatus> agentStatuses, bool isProVersion)
        {
            InitializeComponent();

            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.agentStatuses = agentStatuses ?? new List<AgentStatus>();
            this.isProVersion = isProVersion;

            // Seed UI from settings
            MasterIpTextBox.Text = this.settings.MasterIp ?? string.Empty;
            HeartbeatIntervalTextBox.Text = Math.Max(1000, this.settings.HeartbeatIntervalMs).ToString();
            UseGpuCheckBox.IsChecked = this.settings.UseGpuGlobally;

            // Start role dropdown (0 = Master, 1 = Agent)
            StartRoleCombo.SelectedIndex = this.settings.StartAsMaster ? 0 : 1;

            // Nominated apps collection
            _apps = new ObservableCollection<NominatedApp>(
                (this.settings.NominatedApps ?? new List<NominatedApp>())
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.Path))
                .GroupBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => Normalize(g.First()))
            );
            SelectedAppsListBox.ItemsSource = _apps;
            SelectedAppsListBox.MouseDoubleClick += SelectedAppsListBox_MouseDoubleClick;

            // Security (Join Code) seed
            SeedJoinCodeControls();
        }

        // ---------- Nominated Apps handlers ----------

        private void AddAppButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
                Title = "Select Application"
            };

            if (dialog.ShowDialog() == true)
            {
                var path = dialog.FileName;

                if (_apps.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show("That application is already in the list.", "Duplicate",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var app = Normalize(new NominatedApp
                {
                    Name = Path.GetFileNameWithoutExtension(path),
                    Path = path
                });

                _apps.Add(app);
                SelectedAppsListBox.SelectedItem = app;
            }
        }

        private void RemoveAppButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedAppsListBox.SelectedItem is NominatedApp app)
            {
                _apps.Remove(app);
            }
        }

        private void SelectedAppsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var app = SelectedAppsListBox.SelectedItem as NominatedApp;
            AppNameTextBox.Text = app?.Name ?? string.Empty;
            AppPathTextBox.Text = app?.Path ?? string.Empty;
        }

        // Double-click to launch the selected app
        private void SelectedAppsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SelectedAppsListBox.SelectedItem is not NominatedApp app) return;
            OpenApp(app);
        }

        private void OpenAppButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is NominatedApp app)
                OpenApp(app);
            else if (SelectedAppsListBox.SelectedItem is NominatedApp sel)
                OpenApp(sel);
        }

        private void OpenApp(NominatedApp app)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(app.Path) || !File.Exists(app.Path))
                {
                    MessageBox.Show("The selected application's file was not found:\n" + app.Path,
                        "File Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = app.Path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to launch the application.\n\n" + ex.Message,
                    "Launch Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ---------- Security (Join Code) ----------

        private void SeedJoinCodeControls()
        {
            var code = settings.JoinCode ?? string.Empty;
            code = SecurityManager.NormalizeCode(code);

            JoinCodeSecret.Password = code;
            JoinCodePlain.Text = code;

            JoinCodePlain.Visibility = Visibility.Collapsed;
            JoinCodeSecret.Visibility = Visibility.Visible;
            ShowCodeCheckBox.IsChecked = false;
        }

        private void ShowCodeCheckBox_Checked(object sender, RoutedEventArgs e) => SyncJoinCodeVisibility();
        private void ShowCodeCheckBox_Unchecked(object sender, RoutedEventArgs e) => SyncJoinCodeVisibility();

        private void SyncJoinCodeVisibility()
        {
            bool isShow = ShowCodeCheckBox.IsChecked == true;
            if (isShow)
            {
                JoinCodePlain.Text = JoinCodeSecret.Password;
                JoinCodePlain.Visibility = Visibility.Visible;
                JoinCodeSecret.Visibility = Visibility.Collapsed;
                JoinCodePlain.Focus();
                JoinCodePlain.CaretIndex = JoinCodePlain.Text.Length;
            }
            else
            {
                JoinCodeSecret.Password = JoinCodePlain.Text;
                JoinCodeSecret.Visibility = Visibility.Visible;
                JoinCodePlain.Visibility = Visibility.Collapsed;
                JoinCodeSecret.Focus();
            }
        }

        private void GenerateJoinCodeButton_Click(object sender, RoutedEventArgs e)
        {
            var newCode = SecurityManager.GenerateJoinCode();
            var norm = SecurityManager.NormalizeCode(newCode);
            JoinCodeSecret.Password = norm;
            JoinCodePlain.Text = norm;
        }

        private void CopyJoinCodeButton_Click(object sender, RoutedEventArgs e)
        {
            var code = (ShowCodeCheckBox.IsChecked == true) ? JoinCodePlain.Text : JoinCodeSecret.Password;
            code = SecurityManager.NormalizeCode(code);
            if (!string.IsNullOrWhiteSpace(code))
                Clipboard.SetText(code);
        }

        // ---------- Save / Cancel ----------

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // Validate heartbeat
            if (!int.TryParse(HeartbeatIntervalTextBox.Text, out int interval) || interval < 1000)
            {
                MessageBox.Show("Heartbeat interval must be a number ≥ 1000 ms.", "Invalid Input",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Capture current start-role before overwrite
            bool oldStartAsMaster = settings.StartAsMaster;

            // Persist fields
            settings.MasterIp = (MasterIpTextBox.Text ?? string.Empty).Trim();
            settings.HeartbeatIntervalMs = interval;
            settings.UseGpuGlobally = UseGpuCheckBox.IsChecked == true;

            // Role from dropdown
            settings.StartAsMaster = (StartRoleCombo.SelectedIndex == 0);

            // Security: capture join code
            string captured = ShowCodeCheckBox.IsChecked == true ? (JoinCodePlain.Text ?? "") : (JoinCodeSecret.Password ?? "");
            if (!string.IsNullOrWhiteSpace(captured))
                SecurityManager.TrySetJoinCode(settings, captured);

            // Apps back to settings
            settings.NominatedApps = _apps
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.Path))
                .GroupBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => Normalize(g.First()))
                .ToList();

            // Save to disk
            settings.Save(); // writes settings.json

            // Let MainWindow update live comm security if needed
            if (this.Owner is MainWindow mw)
            {
                try { mw.RefreshCommunicationSecurity(); } catch { /* ignore */ }
            }

            // If role changed, prompt to restart now
            if (oldStartAsMaster != settings.StartAsMaster && this.Owner is MainWindow ownerMw)
            {
                var newRole = settings.StartAsMaster ? NodeRole.Master : NodeRole.Agent;
                var res = MessageBox.Show(
                    $"Role will change to {newRole} on restart.\n\nRestart now?",
                    "Restart Required",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (res == MessageBoxResult.Yes)
                {
                    ownerMw.RestartIntoRole(newRole);
                    return; // won’t reach Close()
                }
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // ---------- Helpers ----------

        private static NominatedApp Normalize(NominatedApp app)
        {
            if (string.IsNullOrWhiteSpace(app.Name) && !string.IsNullOrWhiteSpace(app.Path))
                app.Name = Path.GetFileNameWithoutExtension(app.Path);
            app.Name ??= string.Empty;
            app.Path ??= string.Empty;
            return app;
        }
    }
}








