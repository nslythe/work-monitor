namespace work_monitor
{
    /// <summary>
    /// The kind of input that most recently showed activity.
    /// </summary>
    public enum ActivitySource
    {
        /// <summary>No activity has been detected yet.</summary>
        None,

        /// <summary>The mouse cursor moved.</summary>
        Mouse,

        /// <summary>Input other than cursor movement was detected, typically a key press.</summary>
        Keyboard
    }
}
