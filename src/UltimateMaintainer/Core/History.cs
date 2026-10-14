// History.cs
//
// The run history behind the settings page's History tab: one entry per run of any job (scheduled or Run now, quiet
// nights too) and per Undo, newest first. Kept in history.json in the plugin's data folder.
//
// NOTE: runs/ in each job's folder only keeps runs that changed something (with the full item lists); this file keeps
// every run but only the counts, so it stays small. Capped at Keep entries (3 jobs a night = a bit over two months).

using System;
using System.Collections.Generic;
using System.Linq;

namespace UltimateMaintainer.Core
{
    public class HistoryEntry
    {
        public DateTime DateUtc { get; set; }
        public string Job { get; set; }         // partial-watch, next-up, devices
        public string Kind { get; set; }        // scheduled, manual (Run now), undo
        public int Changed { get; set; }        // for an undo: entries put back
        public string Action { get; set; }      // watched/unwatched, hidden, removed
        public int Recent { get; set; }
        public int Skipped { get; set; }
        public int Errors { get; set; }
    }

    public static class RunHistory
    {
        public const int Keep = 200;

        public const string Scheduled = "scheduled";
        public const string Manual = "manual";
        public const string Undo = "undo";

        // The list with the new entry first, oldest entries dropped beyond Keep.
        public static List<HistoryEntry> Add(IEnumerable<HistoryEntry> list, HistoryEntry entry) =>
            new[] { entry }.Concat((list ?? Enumerable.Empty<HistoryEntry>()).Where(e => e != null))
                .OrderByDescending(e => e.DateUtc).Take(Keep).ToList();
    }
}
