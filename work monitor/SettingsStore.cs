using System;
using System.IO;
using Newtonsoft.Json;

namespace work_monitor
{
    /// <summary>
    /// Loads and saves <see cref="AppSettings"/> as JSON under the user's roaming profile.
    /// </summary>
    public static class SettingsStore
    {
        private const string FolderName = "WorkMonitor";
        private const string FileName = "settings.json";

        private static readonly JsonSerializerSettings JsonSettings = new() { Formatting = Formatting.Indented };

        /// <summary>Directory that holds the settings file.</summary>
        public static string SettingsDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);

        /// <summary>Full path of the settings file.</summary>
        public static string SettingsFilePath => Path.Combine(SettingsDirectory, FileName);

        /// <summary>
        /// Reads the settings file. A missing, unreadable or invalid file yields the defaults,
        /// so the application always starts with usable settings.
        /// </summary>
        public static AppSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsFilePath))
                {
                    return AppSettings.CreateDefault();
                }

                string json = File.ReadAllText(SettingsFilePath);
                AppSettings? settings = JsonConvert.DeserializeObject<AppSettings>(json, JsonSettings);

                return settings is { IsValid: true } ? settings : AppSettings.CreateDefault();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return AppSettings.CreateDefault();
            }
        }

        /// <summary>
        /// Writes the settings file. The file is replaced atomically so a crash cannot leave a
        /// partially written file behind.
        /// </summary>
        public static void Save(AppSettings settings)
        {
            if (settings is null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            Directory.CreateDirectory(SettingsDirectory);

            string tempPath = SettingsFilePath + ".tmp";
            File.WriteAllText(tempPath, JsonConvert.SerializeObject(settings, JsonSettings));
            AtomicFile.Replace(tempPath, SettingsFilePath);
        }
    }
}
