using System;
using System.Runtime.InteropServices;

namespace work_monitor
{
    /// <summary>
    /// Tells whether the workstation is currently locked, meaning the user is not signed in
    /// at an unlocked desktop.
    /// </summary>
    /// <remarks>
    /// The lock state is read from the state Windows records for the session with
    /// <c>WTSQuerySessionInformation</c>. That is more reliable than testing whether the input
    /// desktop can be opened: the Windows 10/11 lock screen can be displayed while the default
    /// desktop is still the input desktop, so <c>OpenInputDesktop</c> alone reports the
    /// workstation as unlocked and input on the lock screen would count as work.
    /// <para>
    /// The input desktop is still checked, because other secure desktops, such as the
    /// Ctrl+Alt+Del screen or a UAC prompt, are not reported as a locked session. When the
    /// session state cannot be read, the input desktop check decides on its own.
    /// </para>
    /// <para>
    /// Input can still be reported during a lock when software other than the user generates it,
    /// so callers should not treat input as a working user while this returns <c>true</c>.
    /// </para>
    /// </remarks>
    internal static class WorkstationLock
    {
        private const uint GenericRead = 0x80000000;

        private const int WtsCurrentSession = -1;
        private const int WtsSessionInfoEx = 25;

        private const int WtsSessionStateLock = 0;
        private const int WtsSessionStateUnlock = 1;
        private const int WtsDisconnected = 4;

        /// <summary>
        /// Whether the lock screen or another secure desktop is showing. When the answer
        /// cannot be read, the workstation is treated as locked, so input is only counted
        /// once an unlocked desktop is confirmed.
        /// </summary>
        public static bool IsLocked()
        {
            bool? sessionLocked = TryReadSessionLocked();

            if (sessionLocked == true)
            {
                return true;
            }

            // A UAC prompt or the Ctrl+Alt+Del screen uses a secure desktop without locking the
            // session, so the input desktop still has to be reachable. When the session state
            // could not be read at all, this is the only answer.
            return !IsInputDesktopAvailable();
        }

        /// <summary>
        /// Reads the lock state Windows records for the current session, or <c>null</c> when it
        /// cannot be read. This does not depend on which desktop currently receives input, so it
        /// also recognises a lock screen that has not been dismissed yet.
        /// </summary>
        private static bool? TryReadSessionLocked()
        {
            IntPtr buffer = IntPtr.Zero;

            try
            {
                if (!WTSQuerySessionInformation(
                        IntPtr.Zero,
                        WtsCurrentSession,
                        WtsSessionInfoEx,
                        out buffer,
                        out int bytesReturned) ||
                    buffer == IntPtr.Zero ||
                    bytesReturned < Marshal.SizeOf<WTSINFOEX>())
                {
                    return null;
                }

                var info = Marshal.PtrToStructure<WTSINFOEX>(buffer);

                if (info.Level != 1)
                {
                    return null;
                }

                if (info.Data.SessionState == WtsDisconnected)
                {
                    // Nobody can be working in a session that is no longer connected, for
                    // example after the console switched to another user or an RDP client left.
                    return true;
                }

                return info.Data.SessionFlags switch
                {
                    WtsSessionStateLock => true,
                    WtsSessionStateUnlock => false,
                    _ => null
                };
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    WTSFreeMemory(buffer);
                }
            }
        }

        private static bool IsInputDesktopAvailable()
        {
            IntPtr desktop = OpenInputDesktop(0, false, GenericRead);

            if (desktop == IntPtr.Zero)
            {
                return false;
            }

            CloseDesktop(desktop);
            return true;
        }

        /// <summary>
        /// A <c>WTSINFOEX</c> header. The <c>DWORD Level</c> is followed by an 8-byte-aligned
        /// union with the level data, so the data starts at offset 8 rather than 4.
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct WTSINFOEX
        {
            [FieldOffset(0)]
            public uint Level;

            [FieldOffset(8)]
            public WTSINFOEX_LEVEL1 Data;
        }

        /// <summary>The leading members of <c>WTSINFOEX_LEVEL1_W</c> needed for the lock state.</summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct WTSINFOEX_LEVEL1
        {
            [FieldOffset(0)]
            public uint SessionId;

            [FieldOffset(4)]
            public int SessionState;

            [FieldOffset(8)]
            public int SessionFlags;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseDesktop(IntPtr desktop);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSQuerySessionInformation(
            IntPtr server,
            int sessionId,
            int infoClass,
            out IntPtr buffer,
            out int bytesReturned);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr memory);
    }
}
