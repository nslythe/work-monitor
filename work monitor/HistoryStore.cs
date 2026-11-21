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
            Append(start, stop, note: null);
        }

        /// <summary>
        /// Adds a work interval with an optional note. The note is copied to every segment when
        /// the interval is split across days.
        /// </summary>
        public static void Append(DateTimeOffset start, DateTimeOffset stop, string? note)
        {
            if (stop <= start)
            {
                return;
            }

            lock (Gate)
            {
                AddInterval(start, stop, note);
            }
        }

        /// <summary>
        /// Adds an interval, split per day like <see cref="Append(DateTimeOffset, DateTimeOffset, string?)"/> does.
        /// The caller must hold <see cref="Gate"/>.
        /// </summary>
        private static void AddInterval(DateTimeOffset start, DateTimeOffset stop, string? note)
        {
            DateTimeOffset segmentStart = start;

            while (segmentStart < stop)
            {
                DateTimeOffset dayEnd = StartOfNextDay(segmentStart);
                DateTimeOffset segmentStop = dayEnd < stop ? dayEnd : stop;

                AddSegment(segmentStart, segmentStop, note);

                segmentStart = segmentStop;
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

        /// <summary>
        /// Replaces the completed record matching the given start and stop times with a new
        /// interval. The new interval is split per day like <see cref="Append"/> does, so a
        /// session that now crosses midnight ends up in the right day(s) and week files.
        /// Returns false when the original record is not found.
        /// </summary>
        public static bool ReplaceRecord(DateTimeOffset oldStart, DateTimeOffset oldStop, DateTimeOffset newStart, DateTimeOffset newStop, string? note)
        {
            if (newStop <= newStart)
            {
                return false;
            }

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
                        int index = day.Records.FindIndex(record => record.Start == oldStart && record.Stop == oldStop);
                        if (index < 0)
                        {
                            continue;
                        }

                        day.Records.RemoveAt(index);

                        // Drop empty days.
                        if (day.Records.Count == 0)
                        {
                            week.Days.Remove(day);
                        }

                        SaveWeekFile(weekId, week);
                        AddInterval(newStart, newStop, note);
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Sets the note on every segment of the session that starts at <paramref name="start"/>.
        /// Segments of a session that crosses midnight are linked by matching start/stop times,
        /// so they all get the same note. Returns false when no matching record is found.
        /// </summary>
        public static bool SetNote(DateTimeOffset start, string? note)
        {
            string? storedNote = NormalizeNote(note);

            lock (Gate)
            {
                var files = new List<(string WeekId, WeekHistory Week)>();
                foreach (string weekId in ListWeeks())
                {
                    if (LoadWeekFile(weekId) is { } week)
                    {
                        files.Add((weekId, week));
                    }
                }

                List<WorkRecord> allRecords = files
                    .SelectMany(file => file.Week.Days)
                    .SelectMany(day => day.Records)
                    .ToList();

                WorkRecord? seed = allRecords.FirstOrDefault(record => record.Start == start);
                if (seed is null)
                {
                    return false;
                }

                // Follow the chain of midnight-split segments in both directions.
                var sessionRecords = new List<WorkRecord> { seed };
                ExtendChain(sessionRecords, allRecords, forward: true);
                ExtendChain(sessionRecords, allRecords, forward: false);

                var changedWeeks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach ((string weekId, WeekHistory week) in files)
                {
                    foreach (WorkRecord record in week.Days.SelectMany(day => day.Records))
                    {
                        if (!sessionRecords.Contains(record))
                        {
                            continue;
                        }

                        record.Note = storedNote;
                        changedWeeks.Add(weekId);
                    }
                }

                foreach (string weekId in changedWeeks)
                {
                    SaveWeekFile(weekId, files.First(file => file.WeekId == weekId).Week);
                }

                return changedWeeks.Count > 0;
            }
        }

        /// <summary>Walks forward or backward from the records already found, adding linked segments.</summary>
        private static void ExtendChain(List<WorkRecord> found, List<WorkRecord> allRecords, bool forward)
        {
            while (true)
            {
                WorkRecord current = forward ? found[found.Count - 1] : found[0];
                DateTimeOffset boundary = forward ? current.Stop ?? DateTimeOffset.MinValue : current.Start;
                WorkRecord? next = allRecords.FirstOrDefault(record =>
                    !found.Contains(record) &&
                    (forward
                        ? record.Start == boundary && record.Stop > boundary
                        : record.Stop == boundary && record.Start < boundary));

                if (next is null)
                {
                    return;
                }

                if (forward)
                {
                    found.Add(next);
                }
                else
                {
                    found.Insert(0, next);
                }
            }
        }

        private static string? NormalizeNote(string? note)
        {
            return string.IsNullOrWhiteSpace(note) ? null : note;
        }

        /// <summary>
        /// Parses a week id like <c>2026-W44</c> back into the Monday that starts it. Returns
        /// false for ids that are not in the expected format or do not round-trip.
        /// </summary>
        public static bool TryGetWeekStartFromId(string weekId, out DateTime weekStart)
        {
            weekStart = default;

            if (weekId.Length != 8 || weekId[4] != '-' || weekId[5] != 'W' ||
                !int.TryParse(weekId.Substring(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out int year) ||
                !int.TryParse(weekId.Substring(6, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int week))
            {
                return false;
            }

            if (year < 1 || week < 1 || week > 53)
            {
                return false;
            }

            // 4 January always falls into ISO week 1, so its Monday starts week 1.
            DateTime firstWeekStart = GetWeekStart(new DateTime(year, 1, 4));
            weekStart = firstWeekStart.AddDays((week - 1) * 7);

            return string.Equals(GetWeekId(weekStart), weekId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Creates the history folder and opens it in Explorer.</summary>
        public static void OpenFolder()
        {
            Directory.CreateDirectory(HistoryDirectory);
            Process.Start(new ProcessStartInfo(HistoryDirectory) { UseShellExecute = true });
        }

        private static void AddSegment(DateTimeOffset start, DateTimeOffset stop, string? note)
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

            day.Records.Add(new WorkRecord { Start = start, Stop = stop, Note = NormalizeNote(note) });
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
