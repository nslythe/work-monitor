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
            if (HasQuitArgument(e.Args))
            {
                // The installer or a script asks a running copy to stop before its files are
                // replaced. Quitting this way flushes the session to history, unlike a kill.
                SingleInstanceGuard.SignalExistingInstanceToQuit();
                Shutdown();
                return;
            }

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

            _instanceGuard.QuitRequested += (_, _) =>
            {
                window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (window.Dispatcher.HasShutdownStarted)
                    {
                        return;
                    }

                    window.PrepareForShutdown();
                }));
            };

            window.Show();
        }

        private static bool HasQuitArgument(string[] arguments)
        {
            foreach (string argument in arguments)
            {
                if (string.Equals(argument, "--quit", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
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
