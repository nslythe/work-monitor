using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace work_monitor
{
    /// <summary>
    /// Asks the user whether to install a newer release. "Update now" downloads the MSI and
    /// leaves the window with <see cref="DownloadedInstallerPath"/> set; the caller starts the
    /// installer and shuts the application down.
    /// </summary>
    public partial class UpdateDialog : Window
    {
        private readonly UpdateInfo _update;
        private readonly CancellationTokenSource _cancellation = new();
        private bool _resolved;
        private bool _busy;

        internal UpdateDialog(UpdateInfo update)
        {
            _update = update ?? throw new ArgumentNullException(nameof(update));

            InitializeComponent();

            HeadingText.Text = $"Work Monitor {update.VersionText} is available";
            VersionText.Text = $"You have version {UpdateService.CurrentVersionText}. " +
                               "Install the new release to get the latest fixes and improvements.";

            ReleaseNotesText.Text = string.IsNullOrWhiteSpace(update.ReleaseNotes)
                ? "No release notes were provided for this version."
                : update.ReleaseNotes;

            if (!update.HasInstaller)
            {
                UpdateNowButton.Content = "Open release page";
                VersionText.Text = $"You have version {UpdateService.CurrentVersionText}. " +
                                   "This release cannot be installed automatically, so open the release page to download it.";
            }
        }

        /// <summary>True when the user chose to be reminded again later (or closed the window).</summary>
        public bool SnoozeRequested { get; private set; }

        /// <summary>True when the user chose to skip this release version.</summary>
        public bool SkipRequested { get; private set; }

        /// <summary>The downloaded MSI, set when "Update now" finished downloading.</summary>
        public string? DownloadedInstallerPath { get; private set; }

        private async void UpdateNowButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                return;
            }

            if (!_update.HasInstaller)
            {
                UpdateService.OpenReleasePage(_update.ReleasePageUrl);
                SnoozeRequested = true;
                _resolved = true;
                DialogResult = false;
                return;
            }

            SetBusy(true);

            try
            {
                var progress = new Progress<double>(value =>
                {
                    if (_cancellation.IsCancellationRequested)
                    {
                        return;
                    }

                    DownloadProgressBar.Value = value * 100;
                    ProgressText.Text = $"Downloading the installer... {value:P0}";
                });

                DownloadedInstallerPath = await UpdateService
                    .DownloadInstallerAsync(_update, progress, _cancellation.Token)
                    .ConfigureAwait(true);

                if (_cancellation.IsCancellationRequested || !IsVisible)
                {
                    return;
                }

                _resolved = true;
                DialogResult = true;
            }
            catch (OperationCanceledException)
            {
                // The window is closing or the user cancelled the download.
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or SecurityException)
            {
                if (_cancellation.IsCancellationRequested)
                {
                    return;
                }

                ErrorText.Text = $"The update could not be downloaded: {ex.Message}";
                ErrorText.Visibility = Visibility.Visible;
                SetBusy(false);
            }
        }

        private void RemindLaterButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                return;
            }

            SnoozeRequested = true;
            _resolved = true;
            DialogResult = false;
        }

        private void SkipButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                return;
            }

            SkipRequested = true;
            _resolved = true;
            DialogResult = false;
        }

        private void ViewReleaseButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateService.OpenReleasePage(_update.ReleasePageUrl);
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && !_busy)
            {
                SnoozeRequested = true;
                _resolved = true;
                DialogResult = false;
                e.Handled = true;
            }
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            _cancellation.Cancel();

            if (!_resolved)
            {
                // Closing with X or Alt+F4 behaves like "Remind me later".
                SnoozeRequested = true;
            }
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;

            UpdateNowButton.IsEnabled = !busy;
            RemindLaterButton.IsEnabled = !busy;
            SkipButton.IsEnabled = !busy;
            ViewReleaseButton.IsEnabled = !busy;

            ErrorText.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            DownloadProgressBar.Value = 0;
            ProgressText.Text = busy ? "Downloading the installer..." : "";
        }
    }
}
