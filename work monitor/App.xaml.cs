using System;
using System.Windows;

namespace work_monitor
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private SingleInstanceGuard? _instanceGuard;

        protected override void OnStartup(StartupEventArgs e)
        {
            _instanceGuard = SingleInstanceGuard.TryAcquire();

            if (_instanceGuard is null)
            {
                // Another copy is already running (possibly hidden in the notification area).
                // Ask it to show its window and exit without creating a second window.
                SingleInstanceGuard.SignalExistingInstance();
                Shutdown();
                return;
            }

            base.OnStartup(e);

            var window = new MainWindow();
            MainWindow = window;

            _instanceGuard.ShowRequested += (_, _) =>
            {
                window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (window.Dispatcher.HasShutdownStarted || !window.IsLoaded)
                    {
                        return;
                    }

                    window.ShowFromTray();
                }));
            };

            window.Show();
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            // Windows is logging off or shutting down: do not hide the window, and let the
            // window close so the current work session is flushed to history.
            if (MainWindow is MainWindow window)
            {
                window.PrepareForShutdown();
            }

            base.OnSessionEnding(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _instanceGuard?.Dispose();
            _instanceGuard = null;

            base.OnExit(e);
        }
    }
}
