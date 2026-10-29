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

        [JsonIgnore]
        public TimeSpan IdleTimeout => TimeSpan.FromMinutes(IdleTimeoutMinutes);

        [JsonIgnore]
        public bool IsValid => IdleTimeoutMinutes is >= MinIdleTimeoutMinutes and <= MaxIdleTimeoutMinutes;

        public static AppSettings CreateDefault() => new();
    }
}
