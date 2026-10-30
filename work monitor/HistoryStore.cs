using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace work_monitor
{
    /// <summary>
    /// Reads and writes the weekly history files under
    /// <c>%APPDATA%\WorkMonitor\history</c>. Each file holds one ISO week and, inside it,
    /// one entry per day with the start/stop records of that day.
    /// </summary>
    public static class HistoryStore
    {
        private const string FolderName = "history";
        private const string FileSuffix = ".json";

        private static readonly object Gate = new();
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.Indented,
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new CamelCaseNamingStrategy()
            }
        };

        /// <summary>Directory that holds the weekly history files.</summary>
        public static string HistoryDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkMonitor", FolderName);

        /// <summary>The Monday that starts the week containing <paramref name="date"/>.</summary>
        public static DateTime GetWeekStart(DateTime date)
        {
            return date.Date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        }

        /// <summary>The ISO week id of the week that starts on <paramref name="weekStart"/>.</summary>
        public static string GetWeekId(DateTime weekStart)
        {
            // ISO 8601: a week belongs to the year that contains its Thursday, and week 1 is
            // the week that contains 4 January.
            DateTime start = GetWeekStart(weekStart);
            int weekYear = start.AddDays(3).Year;
            DateTime firstWeekStart = GetWeekStart(new DateTime(weekYear, 1, 4));
            int weekNumber = ((start - firstWeekStart).Days / 7) + 1;

            return string.Format(CultureInfo.InvariantCulture, "{0:0000}-W{1:00}", weekYear, weekNumber);
        }

        /// <summary>All week ids that have a history file, newest first.</summary>
        public static IReadOnlyList<string> ListWeeks()
        {
            lock (Gate)
            {
                if (!Directory.Exists(HistoryDirectory))
                {
                    return Array.Empty<string>();
                }

                return Directory.EnumerateFiles(HistoryDirectory, "*-W*" + FileSuffix)
                    .Select(path => Path.GetFileNameWithoutExtension(path))
                    .OfType<string>()
                    .Where(name => name.Length > 0)
                    .OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        /// <summary>Loads one week, or <c>null</c> when the file is missing or unreadable.</summary>
        public static WeekHistory? LoadWeek(string weekId)
        {
            lock (Gate)
            {
                return LoadWeekFile(weekId);
            }
        }

        /// <summary>
        /// Adds a work interval. Intervals that cross midnight are split per day, and a Sunday to
        /// Monday interval ends up in two weekly files.
        /// </summary>
        public static void Append(DateTimeOffset start, DateTimeOffset stop)
        {
            if (stop <= start)
            {
                return;
            }

            lock (Gate)
            {
                DateTimeOffset segmentStart = start;

                while (segmentStart < stop)
                {
                    DateTimeOffset dayEnd = StartOfNextDay(segmentStart);
                    DateTimeOffset segmentStop = dayEnd < stop ? dayEnd : stop;

                    AddSegment(segmentStart, segmentStop);

                    segmentStart = segmentStop;
                }
            }
        }

        /// <summary>Returns the most recently completed work record, or <c>null</c> when there is none.</summary>
        public static WorkRecord? GetLastCompletedRecord()
        {
            lock (Gate)
            {
                foreach (string weekId in ListWeeks())
                {
                    WeekHistory? week = LoadWeekFile(weekId);
                    if (week is null)
                    {
                        continue;
                    }

                    foreach (DayHistory day in week.Days.OrderByDescending(entry => entry.Date))
                    {
                        foreach (WorkRecord record in day.Records.OrderByDescending(entry => entry.Stop ?? entry.Start))
                        {
                            if (record.Stop.HasValue)
                            {
                                return record;
                            }
                        }
                    }
                }

                return null;
            }
        }

        /// <summary>Removes the completed record matching the given start and stop times.</summary>
        public static bool RemoveCompletedRecord(DateTimeOffset start, DateTimeOffset stop)
        {
            lock (Gate)
            {
                foreach (string weekId in ListWeeks())
                {
                    WeekHistory? week = LoadWeekFile(weekId);
                    if (week is null)
                    {
                        continue;
                    }

                    foreach (DayHistory day in week.Days)
                    {
                        int index = day.Records.FindIndex(record => record.Start == start && record.Stop == stop);
                        if (index >= 0)
                        {
                            day.Records.RemoveAt(index);
                            SaveWeekFile(weekId, week);
                            return true;
                        }
                    }
                }

                return false;
            }
        }

        /// <summary>Creates the history folder and opens it in Explorer.</summary>
        public static void OpenFolder()
        {
            Directory.CreateDirectory(HistoryDirectory);
            Process.Start(new ProcessStartInfo(HistoryDirectory) { UseShellExecute = true });
        }

        private static void AddSegment(DateTimeOffset start, DateTimeOffset stop)
        {
            DateTime date = start.DateTime.Date;
            DateTime weekStart = GetWeekStart(date);
            string weekId = GetWeekId(weekStart);

            WeekHistory history = LoadWeekFile(weekId) ?? new WeekHistory { WeekStart = weekStart };

            DayHistory? day = history.Days.FirstOrDefault(entry => entry.Date == date);
            if (day is null)
            {
                day = new DayHistory { Date = date };
                history.Days.Add(day);
                history.Days.Sort((left, right) => left.Date.CompareTo(right.Date));
            }

            day.Records.Add(new WorkRecord { Start = start, Stop = stop });
            day.Records.Sort((left, right) => left.Start.CompareTo(right.Start));

            SaveWeekFile(weekId, history);
        }

        private static WeekHistory? LoadWeekFile(string weekId)
        {
            try
            {
                string path = Path.Combine(HistoryDirectory, weekId + FileSuffix);
                if (!File.Exists(path))
                {
                    return null;
                }

                WeekHistory? history = JsonConvert.DeserializeObject<WeekHistory>(File.ReadAllText(path), JsonSettings);
                if (history is null)
                {
                    return null;
                }

                history.Days ??= new List<DayHistory>();
                history.Days.Sort((left, right) => left.Date.CompareTo(right.Date));

                foreach (DayHistory day in history.Days)
                {
                    day.Records ??= new List<WorkRecord>();
                    day.Records.Sort((left, right) => left.Start.CompareTo(right.Start));
                }

                return history;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return null;
            }
        }

        private static void SaveWeekFile(string weekId, WeekHistory history)
        {
            Directory.CreateDirectory(HistoryDirectory);

            string path = Path.Combine(HistoryDirectory, weekId + FileSuffix);
            string tempPath = path + ".tmp";

            File.WriteAllText(tempPath, JsonConvert.SerializeObject(history, JsonSettings));
            AtomicFile.Replace(tempPath, path);
        }

        private static DateTimeOffset StartOfNextDay(DateTimeOffset value)
        {
            DateTime nextMidnight = value.DateTime.Date.AddDays(1);

            return new DateTimeOffset(nextMidnight, value.Offset);
        }
    }
}
