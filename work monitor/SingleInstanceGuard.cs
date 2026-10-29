using System;
using System.IO;
using System.Threading;

namespace work_monitor
{
    /// <summary>
    /// Makes sure only one Work Monitor instance runs per user session. A second launch
    /// signals the running instance to show its window, or to quit when it was started with
    /// <c>--quit</c>, and then exits.
    /// </summary>
    public sealed class SingleInstanceGuard : IDisposable
    {
        private const string MutexName = @"Local\WorkMonitor.SingleInstance";
        private const string ShowEventName = @"Local\WorkMonitor.ShowWindow";
        private const string QuitEventName = @"Local\WorkMonitor.Quit";
        private const int SignalRetryCount = 10;
        private const int SignalRetryDelayMilliseconds = 100;

        private readonly Mutex _mutex;
        private readonly EventWaitHandle _showEvent;
        private readonly EventWaitHandle _quitEvent;
        private readonly EventWaitHandle _stopEvent;
        private readonly Thread _listener;

        private bool _disposed;

        /// <summary>
        /// Raised on a background thread when another launch asks this instance to show its
        /// window. Handlers must marshal to the UI thread.
        /// </summary>
        public event EventHandler? ShowRequested;

        /// <summary>
        /// Raised on a background thread when another launch asks this instance to quit, for
        /// example from the installer before files are replaced. Handlers must marshal to the
        /// UI thread.
        /// </summary>
        public event EventHandler? QuitRequested;

        private SingleInstanceGuard(Mutex mutex, EventWaitHandle showEvent, EventWaitHandle quitEvent, EventWaitHandle stopEvent)
        {
            _mutex = mutex;
            _showEvent = showEvent;
            _quitEvent = quitEvent;
            _stopEvent = stopEvent;
            _listener = new Thread(Listen)
            {
                IsBackground = true,
                Name = "Single instance listener"
            };
            _listener.Start();
        }

        /// <summary>
        /// Returns the guard that owns the single-instance mutex, or <c>null</c> when another
        /// instance is already running.
        /// </summary>
        public static SingleInstanceGuard? TryAcquire()
        {
            var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);

            if (!createdNew)
            {
                mutex.Dispose();
                return null;
            }

            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName, out _);
            var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName, out _);
            var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset);

            return new SingleInstanceGuard(mutex, showEvent, quitEvent, stopEvent);
        }

        /// <summary>Tells the running instance to bring its window back.</summary>
        public static void SignalExistingInstance()
        {
            // The running instance creates the event right after taking the mutex. If a second
            // launch happens in that tiny window, retry briefly instead of losing the request.
            SignalEvent(ShowEventName);
        }

        /// <summary>
        /// Tells the running instance to quit, saving its state and history as if the Quit item
        /// in the notification area had been used.
        /// </summary>
        /// <returns><c>true</c> when a running instance was asked to quit.</returns>
        public static bool SignalExistingInstanceToQuit()
        {
            return SignalEvent(QuitEventName);
        }

        private static bool SignalEvent(string eventName)
        {
            for (int attempt = 0; attempt < SignalRetryCount; attempt++)
            {
                try
                {
                    if (EventWaitHandle.TryOpenExisting(eventName, out EventWaitHandle? handle))
                    {
                        using (handle)
                        {
                            handle.Set();
                        }

                        return true;
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
                {
                    return false;
                }

                Thread.Sleep(SignalRetryDelayMilliseconds);
            }

            return false;
        }

        private void Listen()
        {
            WaitHandle[] handles = { _showEvent, _quitEvent, _stopEvent };

            while (true)
            {
                switch (WaitHandle.WaitAny(handles))
                {
                    case 0:
                        ShowRequested?.Invoke(this, EventArgs.Empty);
                        break;

                    case 1:
                        QuitRequested?.Invoke(this, EventArgs.Empty);
                        break;

                    default:
                        return;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _stopEvent.Set();

            if (_listener.IsAlive && Thread.CurrentThread != _listener)
            {
                _listener.Join(TimeSpan.FromSeconds(2));
            }

            _showEvent.Dispose();
            _quitEvent.Dispose();
            _stopEvent.Dispose();

            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }
}
