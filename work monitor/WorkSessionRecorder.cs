using System;
using System.IO;
using System.Security;

namespace work_monitor
{
    /// <summary>
    /// Turns activity state transitions into "started working" / "stopped working" records.
    /// A session starts on the first activity after an idle period and ends when the monitor
    /// reaches <see cref="WorkState.NotWorking"/>; the recorded stop time is the time of the
    /// last detected activity, not the moment the timeout expired.
    /// </summary>
    public sealed class WorkSessionRecorder : IDisposable
    {
        private readonly MouseActivityMonitor _monitor;
        private readonly object _gate = new();

        private DateTimeOffset? _sessionStart;
        private bool _disposed;

        /// <summary>Raised after a session starts, ends or fails to save. Invoked on the monitor thread.</summary>
        public event EventHandler? HistoryChanged;

        public WorkSessionRecorder(MouseActivityMonitor monitor)
        {
            _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
            _monitor.StateChanged += Monitor_StateChanged;
            _monitor.ActivityDetected += Monitor_ActivityDetected;
        }

        /// <summary>Start of the session that is still running, or <c>null</c> when idle.</summary>
        public DateTimeOffset? CurrentSessionStart
        {
            get
            {
                lock (_gate)
                {
                    return _sessionStart;
                }
            }
        }

        /// <summary>Message of the last failed history write, or <c>null</c> when all writes succeeded.</summary>
        public string? LastError { get; private set; }

        /// <summary>
        /// Saves the session that is still running at its last activity time. Called when the
        /// application closes so work done since the session started is not lost.
        /// </summary>
        public void Flush()
        {
            lock (_gate)
            {
                if (_sessionStart is { } start)
                {
                    _sessionStart = null;

                    Write(start, ToLocal(_monitor.LastActivityUtc));
                }
            }
        }

        /// <summary>
        /// Starts a session on the first input, even when the monitor was already
        /// <see cref="WorkState.Working"/> (for example right after the application starts), so
        /// a continuously active user still gets a history record.
        /// </summary>
        private void Monitor_ActivityDetected(object? sender, EventArgs e)
        {
            bool started = false;

            lock (_gate)
            {
                if (_sessionStart is null)
                {
                    _sessionStart = ToLocal(_monitor.LastActivityUtc);
                    started = true;
                }
            }

            if (started)
            {
                HistoryChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void Monitor_StateChanged(object? sender, WorkStateChangedEventArgs e)
        {
            bool changed = false;

            lock (_gate)
            {
                switch (e.State)
                {
                    case WorkState.Working:
                        if (_sessionStart is null)
                        {
                            _sessionStart = ToLocal(e.LastActivityUtc);
                            changed = true;
                        }

                        break;

                    case WorkState.NotWorking:
                        if (_sessionStart is { } start)
                        {
                            _sessionStart = null;

                            // Use the monitor's timestamp: while activity is continuous no state
                            // transitions are raised, so the event's timestamp can be older than
                            // the actual last activity.
                            Write(start, ToLocal(_monitor.LastActivityUtc));
                            changed = true;
                        }

                        break;
                }
            }

            if (changed)
            {
                HistoryChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void Write(DateTimeOffset start, DateTimeOffset stop)
        {
            if (stop <= start)
            {
                return;
            }

            try
            {
                HistoryStore.Append(start, stop);
                LastError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
            {
                LastError = $"The work history could not be saved: {ex.Message}";
            }
        }

        private static DateTimeOffset ToLocal(DateTime utc)
        {
            DateTime unspecified = DateTime.SpecifyKind(utc, DateTimeKind.Utc);

            return new DateTimeOffset(unspecified).ToLocalTime();
        }

        /// <summary>
        /// If the last completed record ended within the idle timeout, resume it as the
        /// active session instead of starting a new one. Called at application startup.
        /// </summary>
        public void ResumeLastSessionIfNeeded()
        {
            bool changed = false;

            lock (_gate)
            {
                if (_sessionStart is not null)
                {
                    return;
                }

                WorkRecord? last = HistoryStore.GetLastCompletedRecord();
                if (last is null || !last.Stop.HasValue)
                {
                    return;
                }

                if (DateTimeOffset.Now - last.Stop.Value >= _monitor.IdleTimeout)
                {
                    return;
                }

                HistoryStore.RemoveCompletedRecord(last.Start, last.Stop.Value);
                _sessionStart = last.Start;
                changed = true;
            }

            if (changed)
            {
                HistoryChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _monitor.StateChanged -= Monitor_StateChanged;
            _monitor.ActivityDetected -= Monitor_ActivityDetected;

            Flush();
        }
    }
}
