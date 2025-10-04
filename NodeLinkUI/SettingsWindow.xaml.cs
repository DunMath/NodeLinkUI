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
        private readonly bool isProVersion; // kept for signature parity; logic uses settings.ProMode

        // Live collection bound to the ListBox
        private readonly ObservableCollection<NominatedApp> _apps;

        public SettingsWindow(NodeLinkSettings settings, List<AgentStatus> agentStatuses, bool isProVersion)
        {
            InitializeComponent();

            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.agentStatuses = agentStatuses ?? new List<AgentStatus>();
            this.isProVersion = isProVersion;

            // Bind simple fields directly to settings (XAML bindings)
            DataContext = this.settings;

            // Build live collection from persisted apps; filter + de-dupe by Path
            _apps = new ObservableCollection<NominatedApp>(
                (settings.NominatedApps ?? new List<NominatedApp>())
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.Path))
                .GroupBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => Normalize(g.First()))
            );

            // Hook ListBox (do NOT set DisplayMemberPath when using ItemTemplate in XAML)
            SelectedAppsListBox.ItemsSource = _apps;
            SelectedAppsListBox.MouseDoubleClick += SelectedAppsListBox_MouseDoubleClick;

            // Populate PreferredAgent choices only in Pro (if the control exists)
            if (this.settings.ProMode)
            {
                var ids = this.agentStatuses
                    .Where(a => !string.IsNullOrWhiteSpace(a.AgentId))
                    .Select(a => a.AgentId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s)
                    .ToList();

                if (FindName("PreferredAgentComboBox") is ComboBox pref)
                    pref.ItemsSource = ids;
            }

            // --------- Security (Join Code) seed UI if the controls exist ----------
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

                // Prevent duplicates by path
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
            if (SelectedAppsListBox.SelectedItem is NominatedApp app)
            {
                AppNameTextBox.Text = app?.Name ?? string.Empty;
                AppPathTextBox.Text = app?.Path ?? string.Empty;
            }
            else
            {
                AppNameTextBox.Text = string.Empty;
                AppPathTextBox.Text = string.Empty;
            }
        }

        // Double-click to launch the selected app
        private void SelectedAppsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SelectedAppsListBox.SelectedItem is not NominatedApp app) return;
            OpenApp(app);
        }

        // Optional button handler if you add an "Open" button per item
        private void OpenAppButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is NominatedApp app)
                OpenApp(app);
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

        // ---------- Security (Join Code) handlers (safe even if controls aren’t in XAML) ----------

        private void SeedJoinCodeControls()
        {
            var secret = FindName("JoinCodeSecret") as PasswordBox;
            var plain = FindName("JoinCodePlain") as TextBox;
            var show = FindName("ShowCodeCheckBox") as CheckBox;

            if (secret == null || plain == null || show == null)
                return; // XAML not updated yet; skip

            var code = settings.JoinCode ?? string.Empty;
            code = SecurityManager.NormalizeCode(code);

            secret.Password = code;
            plain.Text = code;
            show.IsChecked = false; // start masked
            SyncJoinCodeVisibility();
        }

        // wired up by XAML if present
        private void ShowCodeCheckBox_Checked(object sender, RoutedEventArgs e) => SyncJoinCodeVisibility();
        private void ShowCodeCheckBox_Unchecked(object sender, RoutedEventArgs e) => SyncJoinCodeVisibility();

        private void SyncJoinCodeVisibility()
        {
            var secret = FindName("JoinCodeSecret") as PasswordBox;
            var plain = FindName("JoinCodePlain") as TextBox;
            var show = FindName("ShowCodeCheckBox") as CheckBox;

            if (secret == null || plain == null || show == null)
                return;

            bool isShow = show.IsChecked == true;
            if (isShow)
            {
                plain.Text = secret.Password;
                plain.Visibility = Visibility.Visible;
                secret.Visibility = Visibility.Collapsed;
                plain.Focus();
                plain.CaretIndex = plain.Text.Length;
            }
            else
            {
                secret.Password = plain.Text;
                secret.Visibility = Visibility.Visible;
                plain.Visibility = Visibility.Collapsed;
                secret.Focus();
            }
        }

        // wired up by XAML if present
        private void GenerateJoinCodeButton_Click(object sender, RoutedEventArgs e)
        {
            var secret = FindName("JoinCodeSecret") as PasswordBox;
            var plain = FindName("JoinCodePlain") as TextBox;
            if (secret == null || plain == null) return;

            var newCode = SecurityManager.GenerateJoinCode();
            var norm = SecurityManager.NormalizeCode(newCode);
            secret.Password = norm;
            plain.Text = norm;
        }

        // wired up by XAML if present
        private void CopyJoinCodeButton_Click(object sender, RoutedEventArgs e)
        {
            var secret = FindName("JoinCodeSecret") as PasswordBox;
            var plain = FindName("JoinCodePlain") as TextBox;
            var show = FindName("ShowCodeCheckBox") as CheckBox;
            if (secret == null || plain == null || show == null) return;

            var code = show.IsChecked == true ? plain.Text : secret.Password;
            code = SecurityManager.NormalizeCode(code);
            if (!string.IsNullOrWhiteSpace(code))
                Clipboard.SetText(code);
        }

        // ---------- Save / Cancel ----------

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // Validate heartbeat interval
            if (!int.TryParse(HeartbeatIntervalTextBox.Text, out int interval) || interval < 1000)
            {
                MessageBox.Show("Heartbeat interval must be a number ≥ 1000 ms.", "Invalid Input",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Persist form → settings (bindings also do this; set explicitly for clarity)
            settings.MasterIp = MasterIpTextBox.Text?.Trim() ?? string.Empty;
            settings.HeartbeatIntervalMs = interval;
            settings.UseGpuGlobally = UseGpuGloballyCheck();

            // Security: capture code if controls exist
            string captured = "";
            if (FindName("ShowCodeCheckBox") is CheckBox show)
            {
                if (show.IsChecked == true && FindName("JoinCodePlain") is TextBox plain)
                    captured = plain.Text ?? "";
                else if (FindName("JoinCodeSecret") is PasswordBox secret)
                    captured = secret.Password ?? "";
            }

            if (!string.IsNullOrWhiteSpace(captured))
            {
                SecurityManager.TrySetJoinCode(settings, captured);
            }
            // ProhibitUnauthenticated is bound in XAML; no extra work needed

            // Commit all apps back to settings (persist EVERY item)
            settings.NominatedApps = _apps
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.Path))
                .GroupBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => Normalize(g.First()))
                .ToList();

            settings.Save(); // writes settings.json

            // Apply to live comms without touching private fields
            if (this.Owner is MainWindow mw)
            {
                try { mw.RefreshCommunicationSecurity(); } catch { /* ignore */ }
            }

            DialogResult = true;
            Close();
        }

        private bool UseGpuGloballyCheck()
        {
            return (FindName("UseGpuCheckBox") as CheckBox)?.IsChecked ?? false;
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





