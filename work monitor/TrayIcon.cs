using System;
using System.IO;
using System.Windows;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace work_monitor
{
    /// <summary>
    /// Owns the notification-area icon and its context menu. The icon keeps the application
    /// reachable after the window is hidden and reflects the current work state.
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly Forms.NotifyIcon _notifyIcon;
        private readonly Forms.ContextMenuStrip _menu;
        private readonly Drawing.Icon _workingIcon;
        private readonly Drawing.Icon _notWorkingIcon;

        private bool _hiddenNotificationShown;
        private bool _disposed;

        /// <summary>Raised when the user clicks the icon or chooses Open. Invoked on the UI thread.</summary>
        public event EventHandler? OpenRequested;

        /// <summary>Raised when the user chooses Quit. Invoked on the UI thread.</summary>
        public event EventHandler? QuitRequested;

        /// <param name="initialState">
        /// The state to show right away, so the notification-area icon never starts in the wrong
        /// colour when the application resumes a previous state.
        /// </param>
        public TrayIcon(WorkState initialState)
        {
            _workingIcon = LoadTrayIcon("app.ico");
            _notWorkingIcon = LoadTrayIcon("app.notworking.ico");
            _menu = new Forms.ContextMenuStrip();

            var openItem = new Forms.ToolStripMenuItem("Open Work Monitor")
            {
                Font = new Drawing.Font(_menu.Font, Drawing.FontStyle.Bold)
            };
            openItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

            var quitItem = new Forms.ToolStripMenuItem("Quit Work Monitor");
            quitItem.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);

            _menu.Items.Add(openItem);
            _menu.Items.Add(new Forms.ToolStripSeparator());
            _menu.Items.Add(quitItem);

            _notifyIcon = new Forms.NotifyIcon
            {
                Icon = initialState == WorkState.Working ? _workingIcon : _notWorkingIcon,
                Text = "Work Monitor",
                Visible = true,
                ContextMenuStrip = _menu
            };

            _notifyIcon.MouseClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left)
                {
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                }
            };
        }

        /// <summary>
        /// Switches the notification-area icon to match the work state. Called on the UI thread
        /// whenever the state changes. The tooltip stays fixed because the state is already
        /// carried by the icon.
        /// </summary>
        public void SetState(WorkState state)
        {
            if (_disposed)
            {
                return;
            }

            Drawing.Icon icon = state == WorkState.Working ? _workingIcon : _notWorkingIcon;

            if (!ReferenceEquals(_notifyIcon.Icon, icon))
            {
                _notifyIcon.Icon = icon;
            }
        }

        /// <summary>
        /// Tells the user, once per run, that the window was hidden instead of closed.
        /// </summary>
        public void ShowHiddenNotification()
        {
            if (_hiddenNotificationShown || _disposed)
            {
                return;
            }

            _hiddenNotificationShown = true;

            _notifyIcon.ShowBalloonTip(
                4000,
                "Work Monitor",
                "Still running in the notification area. Right-click the icon and choose Quit to exit.",
                Forms.ToolTipIcon.Info);
        }

        private static Drawing.Icon LoadTrayIcon(string resourceName)
        {
            try
            {
                Stream? stream = Application.GetResourceStream(
                    new Uri($"pack://application:,,,/{resourceName}", UriKind.Absolute))?.Stream;

                if (stream is not null)
                {
                    using (stream)
                    {
                        // Load the frame that matches the notification-area size at the current DPI.
                        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
            {
                // Fall back to the standard icon if the resource cannot be read.
            }

            return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
            _workingIcon.Dispose();
            _notWorkingIcon.Dispose();
        }
    }
}
