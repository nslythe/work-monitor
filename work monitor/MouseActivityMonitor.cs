using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace work_monitor
{
    /// <summary>
    /// Watches mouse movement and keyboard input on a dedicated background thread and reports
    /// whether the user is working. The state is <see cref="WorkState.Working"/> while the
    /// cursor moves or keys are pressed, stays <see cref="WorkState.Working"/> during short
    /// pauses, and becomes <see cref="WorkState.NotWorking"/> once there has been no input for
    /// the configured idle timeout. Any input switches it back to
    /// <see cref="WorkState.Working"/>.
    /// </summary>
    /// <remarks>
    /// The cursor is polled with <c>GetCursorPos</c> and the last input time with
    /// <c>GetLastInputInfo</c>, so activity is detected regardless of whether this application
    /// has focus. No keystrokes are recorded; only the time of the last input is used.
    /// <para>
    /// At startup the state continues from the real time of the last input instead of starting
    /// from a fresh clock, so restarting the application does not turn a user who has been away
    /// into a working one. When Windows reports no input since it started, the time saved by the
    /// previous run is used instead, which keeps the state across a reboot.
    /// </para>
    /// <see cref="StateChanged"/> is raised on the monitor thread; subscribers must marshal to
    /// their own thread if needed.
    /// </remarks>
    public sealed class MouseActivityMonitor : IDisposable
    {
        private const int PollIntervalMilliseconds = 500;

        private readonly Func<(int X, int Y)?> _readCursorPosition;
        private readonly Func<uint?> _readLastInputTick;
        private readonly Func<uint> _readTickCount;
        private readonly Func<DateTimeOffset?> _loadSavedLastActivity;
        private readonly Action<DateTimeOffset> _saveLastActivity;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _stopSignal = new(false);
        private readonly object _gate = new();

        private TimeSpan _idleTimeout;
        private (int X, int Y)? _lastPosition;
        private uint? _lastInputTick;
        private DateTime _lastActivityUtc;
        private ActivitySource _lastActivitySource = ActivitySource.None;
        private WorkState _state = WorkState.Working;
        private bool _started;
        private bool _disposed;

        /// <summary>Raised when the activity state changes. Invoked on the monitor thread.</summary>
        public event EventHandler<WorkStateChangedEventArgs>? StateChanged;

        /// <summary>
        /// Raised whenever new mouse or keyboard input is detected, even when the state stays
        /// <see cref="WorkState.Working"/>. Invoked on the monitor thread.
        /// </summary>
        internal event EventHandler? ActivityDetected;

        /// <param name="idleTimeout">
        /// How long there may be no input before the state becomes
        /// <see cref="WorkState.NotWorking"/>.
        /// </param>
        public MouseActivityMonitor(TimeSpan idleTimeout)
            : this(idleTimeout, null, null, null, null, null)
        {
        }

        /// <param name="idleTimeout">
        /// How long there may be no input before the state becomes
        /// <see cref="WorkState.NotWorking"/>.
        /// </param>
        /// <param name="cursorPositionProvider">
        /// Replaces the real cursor so the transitions can be tested deterministically.
        /// A <c>null</c> result means the position could not be read.
        /// </param>
        /// <param name="lastInputTickProvider">
        /// Replaces the real <c>GetLastInputInfo</c> tick so keyboard activity can be tested
        /// deterministically. A <c>null</c> result means the tick could not be read.
        /// </param>
        /// <param name="tickCountProvider">
        /// Replaces the current <c>GetTickCount</c> value, which is compared with the last input
        /// tick to find out how long the user has been away at startup.
        /// </param>
        /// <param name="loadSavedLastActivity">
        /// Returns the last activity time saved by a previous run, or <c>null</c> when there is none.
        /// </param>
        /// <param name="saveLastActivity">
        /// Persists the last activity time so the next run can resume the state.
        /// </param>
        internal MouseActivityMonitor(
            TimeSpan idleTimeout,
            Func<(int X, int Y)?>? cursorPositionProvider,
            Func<uint?>? lastInputTickProvider,
            Func<uint>? tickCountProvider,
            Func<DateTimeOffset?>? loadSavedLastActivity,
            Action<DateTimeOffset>? saveLastActivity)
        {
            if (idleTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(idleTimeout), idleTimeout, "The idle timeout must be greater than zero.");
            }

            _idleTimeout = idleTimeout;
            _readCursorPosition = cursorPositionProvider ?? ReadCursorPosition;
            _readLastInputTick = lastInputTickProvider ?? ReadLastInputTick;
            _readTickCount = tickCountProvider ?? ReadTickCount;
            _loadSavedLastActivity = loadSavedLastActivity ?? LastActivityStore.Load;
            _saveLastActivity = saveLastActivity ?? LastActivityStore.Save;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Activity monitor"
            };

            ApplyStartupActivity();
        }

        /// <summary>
        /// Rebuilds the last activity time from the real idle duration at startup, so a restart
        /// continues the previous state instead of starting a fresh timeout.
        /// </summary>
        private void ApplyStartupActivity()
        {
            DateTime nowUtc = DateTime.UtcNow;
            DateTime lastActivityUtc = nowUtc;
            uint? inputTick = _readLastInputTick();

            if (inputTick is > 0)
            {
                // GetLastInputInfo reports a tick rather than a time, so the distance to the
                // current tick is how long there has been no input.
                uint nowTick = _readTickCount();
                uint idleMilliseconds = IsNewerTick(nowTick, inputTick.Value)
                    ? unchecked(nowTick - inputTick.Value)
                    : 0;

                lastActivityUtc = nowUtc - TimeSpan.FromMilliseconds(idleMilliseconds);
            }
            else if (_loadSavedLastActivity() is { } saved && saved.UtcDateTime <= nowUtc)
            {
                // Windows reports no input since it started, which is what an automatic start
                // right after a reboot looks like. The previous run knows when the user was
                // last active, so use that instead of treating the boot as activity.
                lastActivityUtc = saved.UtcDateTime;
            }

            _lastActivityUtc = lastActivityUtc;
            _state = nowUtc - lastActivityUtc >= _idleTimeout
                ? WorkState.NotWorking
                : WorkState.Working;
        }

        /// <summary>
        /// How long there may be no input before being considered not working. The value can be
        /// changed at any time; the change applies on the next poll and keeps the time of the
        /// last detected activity intact.
        /// </summary>
        public TimeSpan IdleTimeout
        {
            get
            {
                lock (_gate)
                {
                    return _idleTimeout;
                }
            }

            set
            {
                if (value <= TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "The idle timeout must be greater than zero.");
                }

                lock (_gate)
                {
                    _idleTimeout = value;
                }
            }
        }

        /// <summary>The current activity state.</summary>
        public WorkState State
        {
            get
            {
                lock (_gate)
                {
                    return _state;
                }
            }
        }

        /// <summary>The kind of input that most recently showed activity.</summary>
        public ActivitySource LastActivitySource
        {
            get
            {
                lock (_gate)
                {
                    return _lastActivitySource;
                }
            }
        }

        /// <summary>UTC time of the last detected activity.</summary>
        public DateTime LastActivityUtc
        {
            get
            {
                lock (_gate)
                {
                    return _lastActivityUtc;
                }
            }
        }

        /// <summary>How long there has been no activity at this moment.</summary>
        public TimeSpan IdleFor => DateTime.UtcNow - LastActivityUtc;

        /// <summary>Starts monitoring. Calling this more than once has no effect.</summary>
        public void Start()
        {
            ThrowIfDisposed();

            lock (_gate)
            {
                if (_started)
                {
                    return;
                }

                _started = true;
                _lastActivitySource = ActivitySource.None;
                _lastPosition = _readCursorPosition();
                _lastInputTick = _readLastInputTick();
            }

            _thread.Start();
        }

        /// <summary>Stops monitoring and waits for the monitor thread to finish.</summary>
        public void Stop()
        {
            if (_disposed)
            {
                return;
            }

            SaveLastActivity(LastActivityUtc);
            _stopSignal.Set();

            if (_thread.IsAlive && Thread.CurrentThread != _thread)
            {
                _thread.Join(TimeSpan.FromSeconds(2));
            }
        }

        private void Run()
        {
            // Wait returns true as soon as Stop/Dispose signals, so shutdown is immediate
            // instead of waiting out the remaining poll interval.
            while (!_stopSignal.Wait(PollIntervalMilliseconds))
            {
                Evaluate();
            }
        }

        private void Evaluate()
        {
            DateTime now = DateTime.UtcNow;
            WorkState? changedTo = null;
            ActivitySource source = ActivitySource.None;
            DateTime lastActivity;
            TimeSpan idleFor;

            lock (_gate)
            {
                (int X, int Y)? position = _readCursorPosition();
                uint? inputTick = _readLastInputTick();

                bool cursorMoved = position.HasValue && position.Value != _lastPosition;
                if (position.HasValue)
                {
                    _lastPosition = position;
                }

                // GetLastInputInfo also reflects mouse input, so the tick is stored even when the
                // cursor moved. That keeps a mouse movement from being reported as a key press on
                // the following poll once the cursor is still again.
                bool inputTickAdvanced = inputTick.HasValue &&
                                         _lastInputTick.HasValue &&
                                         inputTick.Value != _lastInputTick.Value &&
                                         IsNewerTick(inputTick.Value, _lastInputTick.Value);

                if (inputTick.HasValue)
                {
                    _lastInputTick = inputTick;
                }

                if (cursorMoved)
                {
                    source = ActivitySource.Mouse;
                }
                else if (inputTickAdvanced)
                {
                    source = ActivitySource.Keyboard;
                }

                if (source != ActivitySource.None)
                {
                    _lastActivityUtc = now;
                    _lastActivitySource = source;
                }

                lastActivity = _lastActivityUtc;
                idleFor = now - lastActivity;

                // Without activity, stay in the current state until the timeout is reached. This
                // keeps short pauses as Working and, if the timeout is raised while already
                // NotWorking, avoids reporting Working without any input.
                WorkState desired = source != ActivitySource.None
                    ? WorkState.Working
                    : idleFor >= _idleTimeout
                        ? WorkState.NotWorking
                        : _state;

                if (desired != _state)
                {
                    _state = desired;
                    changedTo = desired;
                }
            }

            if (source != ActivitySource.None)
            {
                ActivityDetected?.Invoke(this, EventArgs.Empty);
            }

            if (changedTo.HasValue)
            {
                SaveLastActivity(lastActivity);
                StateChanged?.Invoke(this, new WorkStateChangedEventArgs(changedTo.Value, source, lastActivity, idleFor));
            }
        }

        /// <summary>
        /// Persists the last activity time so a later run can resume this state.
        /// </summary>
        private void SaveLastActivity(DateTime lastActivityUtc)
        {
            _saveLastActivity(new DateTimeOffset(DateTime.SpecifyKind(lastActivityUtc, DateTimeKind.Utc)));
        }

        /// <summary>
        /// Compares two GetTickCount values, which wrap around to zero after roughly 49.7 days.
        /// </summary>
        private static bool IsNewerTick(uint current, uint previous)
        {
            return unchecked((int)(current - previous)) > 0;
        }

        private static (int X, int Y)? ReadCursorPosition()
        {
            return GetCursorPos(out POINT point) ? (point.X, point.Y) : null;
        }

        private static uint? ReadLastInputTick()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            return GetLastInputInfo(ref info) ? info.dwTime : null;
        }

        private static uint ReadTickCount()
        {
            // GetLastInputInfo reports the same millisecond tick, so the two values can be
            // subtracted directly. The wrap after ~49.7 days is handled by IsNewerTick.
            return unchecked((uint)Environment.TickCount);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SaveLastActivity(LastActivityUtc);
            _stopSignal.Set();

            if (_thread.IsAlive && Thread.CurrentThread != _thread)
            {
                _thread.Join(TimeSpan.FromSeconds(2));
            }

            _stopSignal.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(MouseActivityMonitor));
            }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }
    }
}
