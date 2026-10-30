using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.IO;
using System.Security;
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

        private readonly AppSettings _settings;
        private readonly MouseActivityMonitor _monitor;
        private readonly WorkSessionRecorder _historyRecorder;
        private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly TrayIcon _trayIcon;

        private bool _loadingHistory;
        private bool _allowClose;

        public MainWindow()
        {
            _settings = SettingsStore.Load();
            _monitor = new MouseActivityMonitor(_settings.IdleTimeout);
            _historyRecorder = new WorkSessionRecorder(_monitor);

            InitializeComponent();

            VersionText.Text = $"Version {Assembly.GetExecutingAssembly().GetName().Version}";

            _trayIcon = new TrayIcon(_monitor.State);
            _trayIcon.OpenRequested += TrayIcon_OpenRequested;
            _trayIcon.QuitRequested += TrayIcon_QuitRequested;

            // Show the resumed state before the window is displayed, so the taskbar and the
            // notification area agree from the first frame.
            Icon = AppIcons.GetWindowIcon(_monitor.State);

            _refreshTimer.Tick += RefreshTimer_Tick;
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
                    StateDetailText.Text = $"No mouse or keyboard activity for {FormatDuration(_monitor.IdleTimeout)}.";
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
            if (_monitor.State == WorkState.Working)
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
                "Mouse movement and keyboard input are checked twice a second on a background thread. " +
                "While the mouse moves or keys are pressed the state is Working, and it stays Working " +
                $"during shorter pauses. After {FormatDuration(_monitor.IdleTimeout)} without any activity " +
                "it becomes Not working. Any activity switches it back to Working. The state is kept when " +
                "the application restarts, including after a reboot, while the timeout has not passed.";
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

        private void RefreshHistory()
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

            LoadSelectedWeek();
        }

        private void LoadSelectedWeek()
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
                            dateLabel,
                            record.Start.LocalDateTime.ToString("HH:mm:ss"),
                            stop.LocalDateTime.ToString("HH:mm:ss"),
                            FormatDurationNoSeconds(duration)));
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
                    FormatDateLabel(sessionDay),
                    sessionStart.LocalDateTime.ToString("HH:mm:ss"),
                    "in progress",
                    FormatDurationNoSeconds(elapsed)));
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

            HistoryListView.ItemsSource = new ListCollectionView(rows)
            {
                GroupDescriptions = { new PropertyGroupDescription(nameof(HistoryRow.DateLabel)) }
            };

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

        private static string FormatDurationNoSeconds(TimeSpan value)
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

    internal sealed class HistoryRow
    {
        public HistoryRow(string dateLabel, string startText, string stopText, string durationText)
        {
            DateLabel = dateLabel;
            StartText = startText;
            StopText = stopText;
            DurationText = durationText;
        }

        public string DateLabel { get; set; }

        public string StartText { get; }

        public string StopText { get; }

        public string DurationText { get; }
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
