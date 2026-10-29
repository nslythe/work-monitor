using System;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace work_monitor
{
    /// <summary>
    /// Adds or removes a per-user startup entry so the application can start when the
    /// user signs in. Uses HKCU, so no administrator rights are required.
    /// </summary>
    public static class StartupRegistration
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "WorkMonitor";

        /// <summary>True when a startup entry exists for the current user.</summary>
        public static bool IsEnabled()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }

        /// <summary>
        /// Registers the currently running executable to start when the user signs in.
        /// Calling this again updates a stale path, for example after the app was moved.
        /// </summary>
        public static void Enable()
        {
            string? executablePath = GetExecutablePath();

            if (string.IsNullOrEmpty(executablePath))
            {
                throw new InvalidOperationException("The path of the running application could not be determined.");
            }

            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("The Windows startup registry key could not be opened.");

            key.SetValue(ValueName, $"\"{executablePath}\"", RegistryValueKind.String);
        }

        /// <summary>Removes the startup entry. Does nothing when it is not present.</summary>
        public static void Disable()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        private static string? GetExecutablePath()
        {
            string? entryAssembly = Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(entryAssembly))
            {
                return entryAssembly;
            }

            try
            {
                return Process.GetCurrentProcess().MainModule?.FileName;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }
    }
}
