using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace work_monitor
{
    /// <summary>
    /// The work records of a single day inside a weekly history file.
    /// </summary>
    public sealed class DayHistory
    {
        [JsonConverter(typeof(IsoDateJsonConverter))]
        public DateTime Date { get; set; }

        public List<WorkRecord> Records { get; set; } = new();
    }
}
