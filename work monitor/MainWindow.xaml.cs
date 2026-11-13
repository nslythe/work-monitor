using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json;

namespace work_monitor
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private static readonly Brush WorkingBackground = new SolidColorBrush(Color.FromRgb(0x1B, 0x7F, 0x3B));
        private static readonly Brush NotWorkingBackground = new SolidColorBrush(Color.FromRgb(0xB3, 0x26, 0x1E));

        private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x1B, 0x7F, 0x3B));
        private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xB3, 0x26, 0x1E));
        private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x61, 0x6E, 0x7C));

        private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);
        private static readonly TimeSpan UpdateSnoozeInterval = TimeSpan.FromHours(24);

        private readonly AppSettings _settings;
        private readonly MouseActivityMonitor _monitor;
        private readonly WorkSessionRecorder _historyRecorder;
        private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _copyFeedbackTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
        private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
        private readonly TrayIcon _trayIcon;

        private bool _loadingHistory;
        private bool _allowClose;
        private bool _checkingForUpdates;
        private bool _updatePromptOpen;
        private string? _rightClickedTimeText;

        public MainWindow()
        {
            _settings = SettingsStore.Load();
            _monitor = new MouseActivityMonitor(_settings.IdleTimeout);
            _historyRecorder = new WorkSessionRecorder(_monitor);

            InitializeComponent();

            VersionText.Text = $"Version {UpdateService.CurrentVersionText}";
            UpdateVersionText.Text = $"Installed version {UpdateService.CurrentVersionText}";

            _trayIcon = new TrayIcon(_monitor.State);
            _trayIcon.OpenRequested += TrayIcon_OpenRequested;
            _trayIcon.QuitRequested += TrayIcon_QuitRequested;

            // Show the resumed state before the window is displayed, so the taskbar and the
            // notification area agree from the first frame.
            Icon = AppIcons.GetWindowIcon(_monitor.State);

            _refreshTimer.Tick += RefreshTimer_Tick;
            _copyFeedbackTimer.Tick += CopyFeedbackTimer_Tick;
            _updateTimer.Tick += UpdateTimer_Tick;
            _monitor.StateChanged += Monitor_StateChanged;
            _historyRecorder.HistoryChanged += HistoryRecorder_HistoryChanged;

            TimeoutTextBox.Text = _settings.IdleTimeoutMinutes.ToString(CultureInfo.InvariantCulture);
            StartWithWindowsCheckBox.IsChecked = TryGetStartupEnabled();
            UpdateFooter();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _monitor.Start();
            _historyRecorder.ResumeLastSessionIfNeeded();
            UpdateStatus(_monitor.State, _monitor.LastActivitySource);
            UpdateActivityInfo();
            RefreshHistory();
            _refreshTimer.Start();

            _updateTimer.Start();
            _ = CheckForUpdatesAsync(manual: false);
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (_allowClose)
            {
                return;
            }

            // Closing with the X keeps the monitor running in the notification area.
            e.Cancel = true;
            Hide();
            _trayIcon.ShowHiddenNotification();
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            _refreshTimer.Stop();
            _refreshTimer.Tick -= RefreshTimer_Tick;

            _updateTimer.Stop();
            _updateTimer.Tick -= UpdateTimer_Tick;

            _trayIcon.Dispose();

            _monitor.StateChanged -= Monitor_StateChanged;
            _monitor.Dispose();

            _historyRecorder.HistoryChanged -= HistoryRecorder_HistoryChanged;
            _historyRecorder.Dispose();
        }

        /// <summary>Brings the window back from the notification area.</summary>
        public void ShowFromTray()
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Show();
            Activate();

            // Briefly toggling Topmost brings the window to the foreground when another
            // application currently has focus.
            Topmost = true;
            Topmost = false;
        }

        /// <summary>
        /// Stops monitoring, flushes the running work session and exits. Used by the Quit item
        /// on the notification-area menu.
        /// </summary>
        public void QuitApplication()
        {
            PrepareForShutdown();
        }

        /// <summary>
        /// Allows the window to close when Windows logs off or shuts down, so the current
        /// session is written to history instead of blocking the shutdown.
        /// </summary>
        public void PrepareForShutdown()
        {
            if (_allowClose)
            {
                return;
            }

            _allowClose = true;
            _trayIcon.Dispose();
            Close();
            Application.Current.Shutdown();
        }

        private void TrayIcon_OpenRequested(object? sender, EventArgs e)
        {
            ShowFromTray();
        }

        private void TrayIcon_QuitRequested(object? sender, EventArgs e)
        {
            QuitApplication();
        }

        private void RefreshTimer_Tick(object? sender, EventArgs e)
        {
            UpdateActivityInfo();
        }

        private void UpdateTimer_Tick(object? sender, EventArgs e)
        {
            _ = CheckForUpdatesAsync(manual: false);
        }

        private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            await CheckForUpdatesAsync(manual: true);
        }

        /// <summary>
        /// Checks GitHub for a newer release. Automatic checks are throttled and stay silent on
        /// failure; a manual check always runs and reports its result in the Settings tab.
        /// </summary>
        private async Task CheckForUpdatesAsync(bool manual)
        {
            if (_checkingForUpdates || _updatePromptOpen || Dispatcher.HasShutdownStarted)
            {
                return;
            }

            if (!manual && !IsAutomaticUpdateCheckDue())
            {
                return;
            }

            _checkingForUpdates = true;

            if (manual)
            {
                CheckForUpdatesButton.IsEnabled = false;
                ShowUpdateStatus("Checking for updates...", isError: false);
            }

            UpdateInfo? update = null;
            string? error = null;

            try
            {
                update = await UpdateService.GetLatestReleaseAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidOperationException or NotSupportedException)
            {
                error = ex.Message;
            }
            finally
            {
                _checkingForUpdates = false;

                if (manual)
                {
                    CheckForUpdatesButton.IsEnabled = true;
                }
            }

            if (Dispatcher.HasShutdownStarted || !IsLoaded)
            {
                return;
            }

            _settings.LastUpdateCheckUtc = DateTime.UtcNow;
            TrySaveSettings();

            if (error is not null)
            {
                if (manual)
                {
                    ShowUpdateStatus($"Could not check for updates: {error}", isError: true);
                }

                return;
            }

            if (update is null)
            {
                if (manual)
                {
                    ShowUpdateStatus("No published releases were found.", isError: false);
                }

                return;
            }

            if (!UpdateService.IsNewer(update.Version, UpdateService.CurrentVersion))
            {
                if (manual)
                {
                    ShowUpdateStatus($"You're up to date (version {UpdateService.CurrentVersionText}).", isError: false);
                }

                return;
            }

            if (manual)
            {
                ShowUpdateStatus($"Version {update.VersionText} is available.", isError: false);
            }

            if (!manual && !ShouldPromptForUpdate(update))
            {
                return;
            }

            ShowUpdatePrompt(update);
        }

        private bool IsAutomaticUpdateCheckDue()
        {
            if (_settings.LastUpdateCheckUtc is not { } lastCheck)
            {
                return true;
            }

            DateTime now = DateTime.UtcNow;

            // A clock change must not postpone checks indefinitely.
            return lastCheck > now || now - lastCheck >= UpdateCheckInterval;
        }

        private bool ShouldPromptForUpdate(UpdateInfo update)
        {
            if (string.Equals(_settings.SkippedUpdateVersion, update.VersionText, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (_settings.UpdateSnoozeUntilUtc is { } snoozeUntil && snoozeUntil > DateTime.UtcNow)
            {
                return false;
            }

            return true;
        }

        private void ShowUpdatePrompt(UpdateInfo update)
        {
            _updatePromptOpen = true;

            try
            {
                if (!IsVisible || WindowState == WindowState.Minimized)
                {
                    ShowFromTray();
                }

                var dialog = new UpdateDialog(update) { Owner = this };
                bool? result = dialog.ShowDialog();

                if (result == true && dialog.DownloadedInstallerPath is { } installerPath)
                {
                    if (UpdateService.TryLaunchInstaller(installerPath, out string? error))
                    {
                        // The installer replaces this executable, so exit after flushing the
                        // running session to history. Its finish page can start the new version.
                        PrepareForShutdown();
                    }
                    else
                    {
                        MessageBox.Show(
                            this,
                            $"The update installer could not be started: {error}",
                            "Update Work Monitor",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                }
                else if (dialog.SkipRequested)
                {
                    _settings.SkippedUpdateVersion = update.VersionText;
                    TrySaveSettings();
                    ShowUpdateStatus($"Version {update.VersionText} will be skipped.", isError: false);
                }
                else if (dialog.SnoozeRequested)
                {
                    _settings.UpdateSnoozeUntilUtc = DateTime.UtcNow.Add(UpdateSnoozeInterval);
                    TrySaveSettings();
                    ShowUpdateStatus($"Version {update.VersionText} will be offered again later.", isError: false);
                }
            }
            finally
            {
                _updatePromptOpen = false;
            }
        }

        private void ShowUpdateStatus(string message, bool isError)
        {
            UpdateStatusText.Text = message;
            UpdateStatusText.Foreground = isError ? ErrorBrush : MutedBrush;
            UpdateStatusText.Visibility = Visibility.Visible;
        }

        private void TrySaveSettings()
        {
            try
            {
                SettingsStore.Save(_settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or SecurityException)
            {
                // Remembering the check time and the user's update choice is best effort.
            }
        }

        /// <summary>
        /// Called on the monitor thread, so the UI update is marshalled to the dispatcher.
        /// </summary>
        private void Monitor_StateChanged(object? sender, WorkStateChangedEventArgs e)
        {
            if (Dispatcher.HasShutdownStarted)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsLoaded)
                {
                    return;
                }

                UpdateStatus(e.State, e.Source);
                UpdateActivityInfo();
            }));
        }

        private void UpdateStatus(WorkState state, ActivitySource source)
        {
            switch (state)
            {
                case WorkState.Working:
                    StatusCard.Background = WorkingBackground;
                    StateText.Text = "Working";
                    StateDetailText.Text = DescribeActivity(source);
                    break;

                default:
                    StatusCard.Background = NotWorkingBackground;
                    StateText.Text = "Not working";
                    StateDetailText.Text = _monitor.IsWorkstationLocked
                        ? "The workstation is locked."
                        : $"No mouse or keyboard activity for {FormatDuration(_monitor.IdleTimeout)}.";
                    break;
            }

            Icon = AppIcons.GetWindowIcon(state);
            Title = $"Work Monitor - {StateText.Text}";
            _trayIcon.SetState(state);
        }

        private static string DescribeActivity(ActivitySource source)
        {
            return source switch
            {
                ActivitySource.Mouse => "Mouse movement detected.",
                ActivitySource.Keyboard => "Keyboard activity detected.",
                _ => "Activity detected."
            };
        }

        private void UpdateActivityInfo()
        {
            LastActivityText.Text = _monitor.LastActivityUtc.ToLocalTime().ToString("HH:mm:ss");
            IdleTimeText.Text = $"{FormatDuration(_monitor.IdleFor)} / {FormatDuration(_monitor.IdleTimeout)}";
            UpdateTodayWorkTime();

            // State changes are only raised on transitions, so keep the description of the most
            // recent activity (mouse or keyboard) current while the state stays Working. During a
            // pause that has not reached the timeout yet, show how long there has been no input.
            // While the workstation is locked, that is shown instead of describing input.
            if (_monitor.IsWorkstationLocked)
            {
                StateDetailText.Text = "The workstation is locked.";
            }
            else if (_monitor.State == WorkState.Working)
            {
                TimeSpan idleFor = _monitor.IdleFor;

                StateDetailText.Text = idleFor >= TimeSpan.FromSeconds(2)
                    ? $"No input for {FormatDuration(idleFor)}. Not working after {FormatDuration(_monitor.IdleTimeout)}."
                    : DescribeActivity(_monitor.LastActivitySource);
            }
        }

        private void UpdateTodayWorkTime()
        {
            DateTime today = DateTime.Today;
            TimeSpan total = TimeSpan.Zero;

            string weekId = HistoryStore.GetWeekId(HistoryStore.GetWeekStart(today));
            if (HistoryStore.LoadWeek(weekId) is { } week)
            {
                foreach (DayHistory day in week.Days)
                {
                    if (day.Date != today)
                    {
                        continue;
                    }

                    foreach (WorkRecord record in day.Records)
                    {
                        if (record.Stop is { } stop)
                        {
                            total += stop - record.Start;
                        }
                    }
                }
            }

            if (_historyRecorder.CurrentSessionStart is { } sessionStart &&
                sessionStart.LocalDateTime.Date == today)
            {
                TimeSpan elapsed = DateTimeOffset.Now - sessionStart;
                if (elapsed < TimeSpan.Zero)
                {
                    elapsed = TimeSpan.Zero;
                }
                total += elapsed;
            }

            TodayWorkTimeCardText.Text = $"Today: {FormatDurationNoSeconds(total)}";
        }

        private void UpdateFooter()
        {
            FooterText.Text =
                "Mouse movement and keyboard input are checked twice a second on a background thread, " +
                "and only counted while the workstation is unlocked. While the mouse moves or keys are " +
                "pressed the state is Working, and it stays Working during shorter pauses. After " +
                $"{FormatDuration(_monitor.IdleTimeout)} without any activity it becomes Not working, and " +
                "locking the workstation does so right away, since input on the lock screen is ignored. " +
                "Any activity while unlocked switches it back to Working. The state is kept when the " +
                "application restarts, including after a reboot, while the timeout has not passed.";
        }

        private void TimeoutTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            bool isValid = IsTimeoutTextValid();

            ApplyButton.IsEnabled = isValid;

            if (isValid || string.IsNullOrWhiteSpace(TimeoutTextBox.Text))
            {
                TimeoutErrorText.Visibility = Visibility.Collapsed;
            }
            else
            {
                TimeoutErrorText.Text =
                    $"Enter a whole number of minutes between {AppSettings.MinIdleTimeoutMinutes} and {AppSettings.MaxIdleTimeoutMinutes}.";
                TimeoutErrorText.Visibility = Visibility.Visible;
            }

            SaveMessageText.Visibility = Visibility.Collapsed;
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadTimeout(out int minutes))
            {
                TimeoutErrorText.Text =
                    $"Enter a whole number of minutes between {AppSettings.MinIdleTimeoutMinutes} and {AppSettings.MaxIdleTimeoutMinutes}.";
                TimeoutErrorText.Visibility = Visibility.Visible;
                return;
            }

            _monitor.IdleTimeout = TimeSpan.FromMinutes(minutes);
            _settings.IdleTimeoutMinutes = minutes;

            var errors = new List<string>();

            try
            {
                SettingsStore.Save(_settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or SecurityException)
            {
                errors.Add($"The settings could not be saved: {ex.Message}");
            }

            try
            {
                if (StartWithWindowsCheckBox.IsChecked == true)
                {
                    StartupRegistration.Enable();
                }
                else
                {
                    StartupRegistration.Disable();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
            {
                errors.Add($"The Windows startup entry could not be updated: {ex.Message}");

                try
                {
                    StartWithWindowsCheckBox.IsChecked = StartupRegistration.IsEnabled();
                }
                catch (Exception)
                {
                    StartWithWindowsCheckBox.IsChecked = false;
                }
            }

            UpdateStatus(_monitor.State, _monitor.LastActivitySource);
            UpdateActivityInfo();
            UpdateFooter();

            if (errors.Count == 0)
            {
                ShowSaveMessage("Settings saved.", isError: false);
            }
            else
            {
                ShowSaveMessage(string.Join(Environment.NewLine, errors), isError: true);
            }
        }

        private void RestoreDefaultsButton_Click(object sender, RoutedEventArgs e)
        {
            TimeoutTextBox.Text = AppSettings.DefaultIdleTimeoutMinutes.ToString(CultureInfo.InvariantCulture);
            StartWithWindowsCheckBox.IsChecked = false;
            ShowSaveMessage("Defaults loaded. Click Apply to save them.", isError: false);
        }

        private bool TryReadTimeout(out int minutes)
        {
            if (int.TryParse(TimeoutTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out minutes) &&
                minutes >= AppSettings.MinIdleTimeoutMinutes &&
                minutes <= AppSettings.MaxIdleTimeoutMinutes)
            {
                return true;
            }

            minutes = _settings.IdleTimeoutMinutes;
            return false;
        }

        private bool IsTimeoutTextValid()
        {
            return int.TryParse(TimeoutTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int minutes) &&
                   minutes >= AppSettings.MinIdleTimeoutMinutes &&
                   minutes <= AppSettings.MaxIdleTimeoutMinutes;
        }

        private void ShowSaveMessage(string message, bool isError)
        {
            SaveMessageText.Text = message;
            SaveMessageText.Foreground = isError ? ErrorBrush : SuccessBrush;
            SaveMessageText.Visibility = Visibility.Visible;
        }

        private static bool TryGetStartupEnabled()
        {
            try
            {
                if (!StartupRegistration.IsEnabled())
                {
                    return false;
                }

                // The executable may have moved since the entry was created, for example when
                // the application was installed or updated, so refresh the stored path.
                StartupRegistration.Enable();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
            {
                return false;
            }
        }

        private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, MainTabs))
            {
                return;
            }

            if (ReferenceEquals(MainTabs.SelectedItem, HistoryTab) && IsLoaded)
            {
                RefreshHistory();
            }
        }

        private void WeekComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingHistory)
            {
                return;
            }

            LoadSelectedWeek();
        }

        private void RefreshHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshHistory();
        }

        private void HistoryDataGrid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _rightClickedTimeText = null;

            if (e.OriginalSource is not DependencyObject source)
            {
                return;
            }

            DataGridRow? row = FindAncestor<DataGridRow>(source);
            if (row?.Item is not HistoryRow historyRow)
            {
                // Right-clicking outside a session row clears the selection, so the context
                // menu actions have nothing to act on.
                HistoryDataGrid.SelectedItem = null;
                return;
            }

            HistoryDataGrid.SelectedItem = historyRow;

            if (FindAncestor<DataGridCell>(source) is { } cell)
            {
                int columnIndex = HistoryDataGrid.Columns.IndexOf(cell.Column);
                _rightClickedTimeText = columnIndex switch
                {
                    0 => historyRow.StartText,
                    1 => historyRow.StopText,
                    _ => null
                };
            }
        }

        private void CopyHistoryTimeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (HistoryDataGrid.SelectedItem is not HistoryRow row)
            {
                return;
            }

            string text = _rightClickedTimeText ?? row.StartText;
            if (text.Length >= 5 && text.Contains(':'))
            {
                string value = text.Substring(0, 5);
                Clipboard.SetText(value);
                CopyStatusText.Text = $"Copied {value}";
                CopyStatusText.Visibility = Visibility.Visible;
                _copyFeedbackTimer.Stop();
                _copyFeedbackTimer.Start();
            }
        }

        private static T? FindAncestor<T>(DependencyObject source) where T : DependencyObject
        {
            DependencyObject? current = source;

            while (current is not null)
            {
                if (current is T match)
                {
                    return match;
                }

                current = current is System.Windows.Media.Visual
                    ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                    : System.Windows.LogicalTreeHelper.GetParent(current);
            }

            return null;
        }

        private void CopyFeedbackTimer_Tick(object? sender, EventArgs e)
        {
            _copyFeedbackTimer.Stop();
            CopyStatusText.Visibility = Visibility.Collapsed;
        }

        private void DeleteHistoryMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (HistoryDataGrid.SelectedItem is HistoryRow row)
            {
                DeleteRow(row);
            }
        }

        private void DeleteRowButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: HistoryRow row })
            {
                DeleteRow(row);
            }
        }

        private void HistoryDataGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // While a cell is being edited, Delete edits text like in any other text box.
            if (e.Key != System.Windows.Input.Key.Delete || e.OriginalSource is TextBox)
            {
                return;
            }

            if (HistoryDataGrid.SelectedItem is HistoryRow row)
            {
                DeleteRow(row);
                e.Handled = true;
            }
        }

        private void DeleteRow(HistoryRow row)
        {
            if (row.IsInProgress || row.Stop is not { } stop)
            {
                return;
            }

            MessageBoxResult confirm = MessageBox.Show(
                "Delete this session?",
                "Delete history record",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                if (HistoryStore.RemoveCompletedRecord(row.Start, stop))
                {
                    UpdateTodayWorkTime();
                    Dispatcher.BeginInvoke(new Action(() => RefreshHistory()));
                }
                else
                {
                    ShowHistoryError("The session could not be found in the history files.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or SecurityException)
            {
                ShowHistoryError($"The session could not be deleted: {ex.Message}");
            }
        }

        private void HistoryDataGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            if (e.Row.Item is HistoryRow { IsInProgress: true })
            {
                e.Cancel = true;
                ShowHistoryError("The session that is currently running cannot be edited while it is being recorded.");
            }
        }

        private void HistoryDataGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit)
            {
                return;
            }

            if (e.Row.Item is not HistoryRow row || row.IsInProgress || row.Stop is not { } originalStop)
            {
                return;
            }

            if (!TryGetUpdatedInterval(row, out DateTimeOffset newStart, out DateTimeOffset newStop, out string error))
            {
                ShowHistoryError(error);
                row.RestorePersistedText();
                return;
            }

            if (newStart == row.Start && newStop == row.Stop)
            {
                return;
            }

            try
            {
                if (!HistoryStore.ReplaceRecord(row.Start, originalStop, newStart, newStop))
                {
                    ShowHistoryError("The session could not be found in the history files.");
                    row.RestorePersistedText();
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or SecurityException)
            {
                ShowHistoryError($"The session could not be saved: {ex.Message}");
                row.RestorePersistedText();
                return;
            }

            row.Start = newStart;
            row.Stop = newStop;
            UpdateTodayWorkTime();
            Dispatcher.BeginInvoke(new Action(() => RefreshHistory(newStart)));
        }

        /// <summary>Parses the edited cell texts of a row into a new start/stop pair on the row's day.</summary>
        private bool TryGetUpdatedInterval(HistoryRow row, out DateTimeOffset newStart, out DateTimeOffset newStop, out string error)
        {
            newStart = default;
            newStop = default;

            if (!TryParseTimeOfDay(row.StartText, out TimeSpan startTime) ||
                !TryParseTimeOfDay(row.StopText, out TimeSpan stopTime))
            {
                error = "Enter times as HH:mm or HH:mm:ss, for example 08:30 or 08:30:15.";
                return false;
            }

            newStart = new DateTimeOffset(row.Date + startTime);
            newStop = new DateTimeOffset(row.Date + stopTime);

            if (newStop <= newStart)
            {
                error = "Stopped must be after Started.";
                return false;
            }

            if (newStop > DateTimeOffset.Now)
            {
                error = "Times cannot be in the future.";
                return false;
            }

            error = "";
            return true;
        }

        /// <summary>
        /// Accepts clock times as HH:mm or HH:mm:ss. "24:00" means midnight at the end of the day.
        /// </summary>
        internal static bool TryParseTimeOfDay(string text, out TimeSpan timeOfDay)
        {
            timeOfDay = TimeSpan.Zero;

            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            text = text.Trim();

            if (string.Equals(text, "24:00", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "24:00:00", StringComparison.OrdinalIgnoreCase))
            {
                timeOfDay = TimeSpan.FromDays(1);
                return true;
            }

            if (DateTime.TryParseExact(
                    text,
                    new[] { "HH:mm:ss", "H:m:s", "HH:mm", "H:m" },
                    CultureInfo.CurrentCulture,
                    DateTimeStyles.NoCurrentDateDefault,
                    out DateTime parsed))
            {
                timeOfDay = parsed.TimeOfDay;
                return true;
            }

            return false;
        }

        private void ShowHistoryError(string message)
        {
            HistoryErrorText.Text = message;
            HistoryErrorText.Foreground = ErrorBrush;
            HistoryErrorText.Visibility = Visibility.Visible;
        }

        private void AddSessionButton_Click(object sender, RoutedEventArgs e)
        {
            if (WeekComboBox.SelectedItem is not WeekOption option ||
                !HistoryStore.TryGetWeekStartFromId(option.Id, out DateTime weekStart))
            {
                return;
            }

            // A small day picker, so sessions can also be added to days that have no
            // group in the table yet.
            var dayMenu = new ContextMenu();
            DateTime today = DateTime.Today;

            for (int dayIndex = 0; dayIndex < 7; dayIndex++)
            {
                DateTime date = weekStart.AddDays(dayIndex);
                string header = FormatDateLabel(date);
                if (date == today)
                {
                    header += "  (today)";
                }

                DateTime captured = date;
                var item = new MenuItem { Header = header };
                item.Click += (clickSender, clickArgs) => AddSession(captured);
                dayMenu.Items.Add(item);
            }

            if (sender is Button addButton)
            {
                dayMenu.PlacementTarget = addButton;
            }

            dayMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            dayMenu.IsOpen = true;
        }

        private void AddSessionForGroupButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: CollectionViewGroup group })
            {
                foreach (object item in group.Items)
                {
                    if (item is HistoryRow row)
                    {
                        AddSession(row.Date);
                        return;
                    }
                }
            }
        }

        private void AddSession(DateTime date)
        {
            DateTimeOffset start;
            DateTimeOffset stop;

            if (date == DateTime.Today)
            {
                // For today a session that ends now is the most useful starting point.
                stop = DateTimeOffset.Now;
                start = stop - TimeSpan.FromHours(1);
            }
            else
            {
                start = new DateTimeOffset(date.AddHours(9));
                stop = new DateTimeOffset(date.AddHours(10));
            }

            try
            {
                HistoryStore.Append(start, stop);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or SecurityException)
            {
                ShowHistoryError($"The session could not be added: {ex.Message}");
                return;
            }

            UpdateTodayWorkTime();
            // The deferred refresh selects the new row and opens its first cell for editing.
            Dispatcher.BeginInvoke(new Action(() => RefreshHistory(start)));
        }

        private void ArchiveHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            string zipPath = Path.Combine(Path.GetDirectoryName(HistoryStore.HistoryDirectory)!, "archive.zip");
            try
            {
                if (File.Exists(zipPath))
                {
                    File.Delete(zipPath);
                }

                if (Directory.Exists(HistoryStore.HistoryDirectory) && Directory.EnumerateFileSystemEntries(HistoryStore.HistoryDirectory).Any())
                {
                    ZipFile.CreateFromDirectory(HistoryStore.HistoryDirectory, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
                }
                else
                {
                    using (FileStream fs = File.Create(zipPath))
                    using (ZipArchive archive = new ZipArchive(fs, ZipArchiveMode.Create))
                    {
                    }
                }

                HistoryErrorText.Text = $"History archived to {zipPath}";
                HistoryErrorText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1F, 0x29, 0x33));
                HistoryErrorText.Visibility = Visibility.Visible;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                HistoryErrorText.Text = $"The history could not be archived: {ex.Message}";
                HistoryErrorText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB3, 0x26, 0x1E));
                HistoryErrorText.Visibility = Visibility.Visible;
            }
        }

        private void OpenHistoryFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                HistoryStore.OpenFolder();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                HistoryErrorText.Text = $"The history folder could not be opened: {ex.Message}";
                HistoryErrorText.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// Called on the monitor thread after a session is recorded, so the refresh is
        /// marshalled to the dispatcher.
        /// </summary>
        private void HistoryRecorder_HistoryChanged(object? sender, EventArgs e)
        {
            if (Dispatcher.HasShutdownStarted)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsLoaded)
                {
                    return;
                }

                RefreshHistory();
                UpdateTodayWorkTime();
            }));
        }

        private void RefreshHistory(DateTimeOffset? selectStart = null)
        {
            string? selectedId = (WeekComboBox.SelectedItem as WeekOption)?.Id;

            DateTime today = DateTime.Today;
            string currentWeekId = HistoryStore.GetWeekId(HistoryStore.GetWeekStart(today));

            var weekIds = new List<string> { currentWeekId };
            foreach (string weekId in HistoryStore.ListWeeks())
            {
                if (!weekIds.Contains(weekId, StringComparer.OrdinalIgnoreCase))
                {
                    weekIds.Add(weekId);
                }
            }

            var options = new List<WeekOption>();
            foreach (string weekId in weekIds)
            {
                string label = string.Equals(weekId, currentWeekId, StringComparison.OrdinalIgnoreCase)
                    ? $"This week ({weekId})"
                    : weekId;

                options.Add(new WeekOption(weekId, label));
            }

            _loadingHistory = true;
            try
            {
                WeekComboBox.ItemsSource = options;
                WeekComboBox.SelectedItem = options.Find(option => option.Id == selectedId) ?? options[0];
            }
            finally
            {
                _loadingHistory = false;
            }

            LoadSelectedWeek(selectStart);
        }

        private void LoadSelectedWeek(DateTimeOffset? selectStart = null)
        {
            var rows = new List<HistoryRow>();
            TimeSpan todayTotal = TimeSpan.Zero;
            TimeSpan weekTotal = TimeSpan.Zero;
            DateTime today = DateTime.Today;
            var dayTotals = new Dictionary<DateTime, TimeSpan>();

            if (WeekComboBox.SelectedItem is WeekOption option &&
                HistoryStore.LoadWeek(option.Id) is { } week)
            {
                foreach (DayHistory day in week.Days)
                {
                    string dateLabel = FormatDateLabel(day.Date);

                    foreach (WorkRecord record in day.Records)
                    {
                        if (record.Stop is not { } stop)
                        {
                            continue;
                        }

                        TimeSpan duration = stop - record.Start;
                        weekTotal += duration;

                        if (!dayTotals.ContainsKey(day.Date))
                        {
                            dayTotals[day.Date] = TimeSpan.Zero;
                        }
                        dayTotals[day.Date] += duration;

                        if (day.Date == today)
                        {
                            todayTotal += duration;
                        }

                        rows.Add(new HistoryRow(
                            day.Date,
                            dateLabel,
                            record.Start.LocalDateTime.ToString("HH:mm:ss"),
                            stop.LocalDateTime.ToString("HH:mm:ss"),
                            FormatDurationNoSeconds(duration),
                            record.Start,
                            record.Stop,
                            isInProgress: false));
                    }
                }
            }

            // Show the session that is currently running, if it belongs to the selected week.
            if (WeekComboBox.SelectedItem is WeekOption selectedWeek &&
                _historyRecorder.CurrentSessionStart is { } sessionStart &&
                IsInWeek(sessionStart, selectedWeek.Id))
            {
                TimeSpan elapsed = DateTimeOffset.Now - sessionStart;
                if (elapsed < TimeSpan.Zero)
                {
                    elapsed = TimeSpan.Zero;
                }

                DateTime sessionDay = sessionStart.LocalDateTime.Date;
                weekTotal += elapsed;

                if (!dayTotals.ContainsKey(sessionDay))
                {
                    dayTotals[sessionDay] = TimeSpan.Zero;
                }
                dayTotals[sessionDay] += elapsed;

                if (sessionDay == today)
                {
                    todayTotal += elapsed;
                }

                rows.Add(new HistoryRow(
                    sessionDay,
                    FormatDateLabel(sessionDay),
                    sessionStart.LocalDateTime.ToString("HH:mm:ss"),
                    "in progress",
                    FormatDurationNoSeconds(elapsed),
                    sessionStart,
                    null,
                    isInProgress: true));
            }

            // Append per-day totals to the group (date) labels.
            var dayTotalByLabel = new Dictionary<string, TimeSpan>();
            foreach (KeyValuePair<DateTime, TimeSpan> kv in dayTotals)
            {
                dayTotalByLabel[FormatDateLabel(kv.Key)] = kv.Value;
            }

            foreach (HistoryRow row in rows)
            {
                if (dayTotalByLabel.TryGetValue(row.DateLabel, out TimeSpan dayTotal))
                {
                    row.DateLabel = $"{row.DateLabel}  —  Total: {FormatDurationNoSeconds(dayTotal)}";
                }
            }

            HistoryDataGrid.ItemsSource = new ListCollectionView(rows)
            {
                GroupDescriptions = { new PropertyGroupDescription(nameof(HistoryRow.DateLabel)) }
            };

            if (selectStart is { } startToSelect)
            {
                SelectAndEditRow(rows, startToSelect);
            }

            HistoryEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HistorySummaryText.Text = $"Today: {FormatDurationNoSeconds(todayTotal)}    Week total: {FormatDurationNoSeconds(weekTotal)}";

            if (_historyRecorder.CurrentSessionStart is { } currentStart)
            {
                CurrentSessionText.Text = $"Current session started {currentStart.LocalDateTime:HH:mm:ss}";
                CurrentSessionText.Visibility = Visibility.Visible;
            }
            else
            {
                CurrentSessionText.Visibility = Visibility.Collapsed;
            }

            if (string.IsNullOrEmpty(_historyRecorder.LastError))
            {
                HistoryErrorText.Visibility = Visibility.Collapsed;
            }
            else
            {
                HistoryErrorText.Text = _historyRecorder.LastError;
                HistoryErrorText.Visibility = Visibility.Visible;
            }
        }

        /// <summary>Selects the row of a newly added or edited record and opens its first cell for editing.</summary>
        private void SelectAndEditRow(List<HistoryRow> rows, DateTimeOffset start)
        {
            HistoryRow? row = rows.FirstOrDefault(candidate => candidate.Start == start);
            if (row is null)
            {
                return;
            }

            HistoryDataGrid.SelectedItem = row;
            HistoryDataGrid.CurrentCell = new DataGridCellInfo(row, HistoryDataGrid.Columns[0]);
            HistoryDataGrid.Focus();
            HistoryDataGrid.BeginEdit();
        }

        private static bool IsInWeek(DateTimeOffset value, string weekId)
        {
            DateTime date = value.LocalDateTime.Date;
            string valueWeekId = HistoryStore.GetWeekId(HistoryStore.GetWeekStart(date));

            return string.Equals(valueWeekId, weekId, StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatDateLabel(DateTime date)
        {
            return date.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture);
        }

        private static string FormatDuration(TimeSpan value)
        {
            if (value < TimeSpan.Zero)
            {
                value = TimeSpan.Zero;
            }

            int hours = (int)value.TotalHours;

            return hours > 0
                ? $"{hours}h {value.Minutes:00}m {value.Seconds:00}s"
                : $"{(int)value.TotalMinutes}m {value.Seconds:00}s";
        }

        internal static string FormatDurationNoSeconds(TimeSpan value)
        {
            if (value < TimeSpan.Zero)
            {
                value = TimeSpan.Zero;
            }

            int hours = (int)value.TotalHours;

            return hours > 0
                ? $"{hours}h {value.Minutes:00}m"
                : $"{(int)value.TotalMinutes}m";
        }
    }

    /// <summary>
    /// One row of the history table. The texts can be edited in place; the start and stop
    /// properties keep the persisted values that identify the record in the store.
    /// </summary>
    internal sealed class HistoryRow : INotifyPropertyChanged, IEditableObject
    {
        private string _startText;
        private string _stopText;
        private string _durationText;
        private (string StartText, string StopText)? _editSnapshot;

        public HistoryRow(DateTime date, string dateLabel, string startText, string stopText, string durationText, DateTimeOffset start, DateTimeOffset? stop, bool isInProgress)
        {
            Date = date;
            DateLabel = dateLabel;
            _startText = startText;
            _stopText = stopText;
            _durationText = durationText;
            Start = start;
            Stop = stop;
            IsInProgress = isInProgress;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>The local day this row belongs to.</summary>
        public DateTime Date { get; }

        /// <summary>The group header label, including the day total.</summary>
        public string DateLabel { get; set; }

        /// <summary>The persisted start of the record. Updated after an edit is saved.</summary>
        public DateTimeOffset Start { get; internal set; }

        /// <summary>The persisted stop of the record. Updated after an edit is saved.</summary>
        public DateTimeOffset? Stop { get; internal set; }

        /// <summary>True while the session is still running and cannot be edited.</summary>
        public bool IsInProgress { get; }

        public bool IsEditable => !IsInProgress;

        public string StartText
        {
            get => _startText;
            set
            {
                if (_startText != value)
                {
                    _startText = value;
                    OnPropertyChanged(nameof(StartText));
                    UpdateDuration();
                }
            }
        }

        public string StopText
        {
            get => _stopText;
            set
            {
                if (_stopText != value)
                {
                    _stopText = value;
                    OnPropertyChanged(nameof(StopText));
                    UpdateDuration();
                }
            }
        }

        public string DurationText => _durationText;

        /// <summary>
        /// Restores the cell texts from the persisted start and stop, used after a rejected
        /// edit.
        /// </summary>
        public void RestorePersistedText()
        {
            StartText = Start.LocalDateTime.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
            if (Stop is { } stop)
            {
                StopText = stop.LocalDateTime.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
            }
        }

        void IEditableObject.BeginEdit()
        {
            _editSnapshot ??= (_startText, _stopText);
        }

        void IEditableObject.CancelEdit()
        {
            if (_editSnapshot is { } snapshot)
            {
                _startText = snapshot.StartText;
                _stopText = snapshot.StopText;
                _editSnapshot = null;
                OnPropertyChanged(nameof(StartText));
                OnPropertyChanged(nameof(StopText));
                UpdateDuration();
            }
        }

        void IEditableObject.EndEdit()
        {
            _editSnapshot = null;
        }

        private void UpdateDuration()
        {
            string computed;

            if (MainWindow.TryParseTimeOfDay(_startText, out TimeSpan startTime) &&
                MainWindow.TryParseTimeOfDay(_stopText, out TimeSpan stopTime) &&
                stopTime > startTime)
            {
                computed = MainWindow.FormatDurationNoSeconds(stopTime - startTime);
            }
            else if (Stop is { } stop)
            {
                // Half-finished or invalid cell texts keep showing the persisted duration.
                computed = MainWindow.FormatDurationNoSeconds(stop - Start);
            }
            else
            {
                return;
            }

            if (_durationText != computed)
            {
                _durationText = computed;
                OnPropertyChanged(nameof(DurationText));
            }
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    internal sealed class WeekOption
    {
        public WeekOption(string id, string label)
        {
            Id = id;
            Label = label;
        }

        public string Id { get; }

        public string Label { get; }

        public override string ToString() => Label;
    }
}
