using System;
using Newtonsoft.Json;

namespace work_monitor
{
    /// <summary>
    /// Settings that are persisted between runs of the application.
    /// </summary>
    public sealed class AppSettings
    {
        public const int DefaultIdleTimeoutMinutes = 15;
        public const int MinIdleTimeoutMinutes = 1;
        public const int MaxIdleTimeoutMinutes = 1440;

        /// <summary>Minutes without mouse movement before the state becomes Not working.</summary>
        public int IdleTimeoutMinutes { get; set; } = DefaultIdleTimeoutMinutes;

        /// <summary>Release version the user asked to skip, so it is not offered again.</summary>
        public string? SkippedUpdateVersion { get; set; }

        /// <summary>UTC time of the most recent update check, whether it succeeded or not.</summary>
        public DateTime? LastUpdateCheckUtc { get; set; }

        /// <summary>UTC time before which no update prompt is shown again.</summary>
        public DateTime? UpdateSnoozeUntilUtc { get; set; }

        [JsonIgnore]
        public TimeSpan IdleTimeout => TimeSpan.FromMinutes(IdleTimeoutMinutes);

        [JsonIgnore]
        public bool IsValid => IdleTimeoutMinutes is >= MinIdleTimeoutMinutes and <= MaxIdleTimeoutMinutes;

        public static AppSettings CreateDefault() => new();
    }
}
