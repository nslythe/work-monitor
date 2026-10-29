using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace work_monitor
{
    /// <summary>
    /// One weekly history file. The week id (for example <c>2026-W44</c>) is derived from
    /// <see cref="WeekStart"/>, which is always a Monday.
    /// </summary>
    public sealed class WeekHistory
    {
        [JsonConverter(typeof(IsoDateJsonConverter))]
        public DateTime WeekStart { get; set; }

        public List<DayHistory> Days { get; set; } = new();

        [JsonIgnore]
        public string WeekId => HistoryStore.GetWeekId(WeekStart);
    }
}
