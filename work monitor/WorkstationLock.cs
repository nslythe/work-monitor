using System;
using System.Runtime.InteropServices;

namespace work_monitor
{
    /// <summary>
    /// Tells whether the workstation is currently locked, meaning the user is not signed in
    /// at an unlocked desktop.
    /// </summary>
    /// <remarks>
    /// While the workstation is locked, Windows delivers input to a secure desktop that
    /// applications cannot open, so an attempt to open the input desktop fails. The same
    /// happens while other secure desktops are showing, such as the Ctrl+Alt+Del screen or
    /// a UAC prompt. Input can still be reported during a lock when software other than the
    /// user generates it, so callers should not treat input as a working user while this
    /// returns <c>true</c>.
    /// </remarks>
    internal static class WorkstationLock
    {
        private const uint GenericRead = 0x80000000;

        /// <summary>
        /// Whether the lock screen or another secure desktop is showing. When the answer
        /// cannot be read, the workstation is treated as locked, so input is only counted
        /// once an unlocked desktop is confirmed.
        /// </summary>
        public static bool IsLocked()
        {
            IntPtr desktop = OpenInputDesktop(0, false, GenericRead);

            if (desktop == IntPtr.Zero)
            {
                return true;
            }

            CloseDesktop(desktop);
            return false;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseDesktop(IntPtr desktop);
    }
}
