using System;
using Newtonsoft.Json;

namespace work_monitor
{
    /// <summary>
    /// One "started working" to "stopped working" interval as stored in the history files.
    /// </summary>
    public sealed class WorkRecord
    {
        /// <summary>When activity started.</summary>
        public DateTimeOffset Start { get; set; }

        /// <summary>When activity last happened before the timeout, or <c>null</c> while in progress.</summary>
        public DateTimeOffset? Stop { get; set; }

        [JsonIgnore]
        public TimeSpan Duration => Stop.HasValue ? Stop.Value - Start : TimeSpan.Zero;
    }
}
