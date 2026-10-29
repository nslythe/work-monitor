using System;

namespace work_monitor
{
    /// <summary>
    /// The activity state reported by <see cref="MouseActivityMonitor"/>.
    /// </summary>
    public enum WorkState
    {
        /// <summary>
        /// The mouse is moving or keys are being pressed, or input has stopped but the idle
        /// timeout has not been reached yet.
        /// </summary>
        Working,

        /// <summary>There has been no mouse or keyboard activity within the idle timeout.</summary>
        NotWorking
    }

    /// <summary>
    /// Describes a transition of the activity state.
    /// </summary>
    public sealed class WorkStateChangedEventArgs : EventArgs
    {
        public WorkStateChangedEventArgs(WorkState state, ActivitySource source, DateTime lastActivityUtc, TimeSpan idleFor)
        {
            State = state;
            Source = source;
            LastActivityUtc = lastActivityUtc;
            IdleFor = idleFor;
        }

        /// <summary>The state the monitor just switched to.</summary>
        public WorkState State { get; }

        /// <summary>The input that caused the transition, if any.</summary>
        public ActivitySource Source { get; }

        /// <summary>UTC time of the last detected activity.</summary>
        public DateTime LastActivityUtc { get; }

        /// <summary>How long there had been no activity when the transition happened.</summary>
        public TimeSpan IdleFor { get; }
    }
}
