using System;
using System.IO;
using Newtonsoft.Json;

namespace work_monitor
{
    /// <summary>
    /// Remembers when input was last seen so a restarted application can resume the work state
    /// instead of starting from a fresh idle clock. This matters after a reboot, when the
    /// system's own last-input time only covers the period since Windows started.
    /// </summary>
    /// <remarks>
    /// This is runtime state, not user configuration, so it lives in its own file and every
    /// failure is treated as "no saved value". Losing it only costs state continuity.
    /// </remarks>
    internal static class LastActivityStore
    {
        private const string FileName = "last-activity.json";

        private static readonly JsonSerializerSettings JsonSettings = new() { Formatting = Formatting.Indented };

        /// <summary>Full path of the file that holds the last activity time.</summary>
        public static string FilePath => Path.Combine(SettingsStore.SettingsDirectory, FileName);

        /// <summary>
        /// Reads the saved activity time, or <c>null</c> when it is missing or unreadable.
        /// Values from the future (for example after the clock was moved back) are ignored.
        /// </summary>
        public static DateTimeOffset? Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return null;
                }

                string json = File.ReadAllText(FilePath);
                LastActivity? saved = JsonConvert.DeserializeObject<LastActivity>(json, JsonSettings);

                if (saved is not { LastActivityUtc: var value } || value > DateTimeOffset.UtcNow)
                {
                    return null;
                }

                return value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return null;
            }
        }

        /// <summary>
        /// Saves the activity time. Best effort: a failure here never interrupts monitoring.
        /// </summary>
        public static void Save(DateTimeOffset lastActivityUtc)
        {
            try
            {
                Directory.CreateDirectory(SettingsStore.SettingsDirectory);

                string tempPath = FilePath + ".tmp";
                File.WriteAllText(
                    tempPath,
                    JsonConvert.SerializeObject(new LastActivity { LastActivityUtc = lastActivityUtc }, JsonSettings));
                AtomicFile.Replace(tempPath, FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
            {
                // The state is only an optimisation for the next start; keep running without it.
            }
        }

        private sealed class LastActivity
        {
            public DateTimeOffset LastActivityUtc { get; set; }
        }
    }
}
