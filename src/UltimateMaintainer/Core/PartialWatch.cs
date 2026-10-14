// PartialWatch.cs
//
// Partial-watch cleanup: an item a user started but left with a resume point (it sits in their Continue Watching)
// and has not played for N days is either marked watched or has its resume point cleared ("unwatched"). One action
// for every item, chosen by the admin; per user, so one user's leftovers never touch another user's.
//
// Plan() only looks (Preview uses it as is); Apply() changes what Plan() found and returns a RunRecord with the old
// values, which Undo() puts back.
//
// NOTE: an item already marked played can have a resume point too (a rewatch stopped halfway; about a third of
// all partial items on a real server, 2026-10-12). For those both actions only clear the resume point: "unwatched" never takes back a "watched" a user
// already earned. Items without a last-played date are skipped (no clock to time out from) and listed instead.

using System;
using System.Collections.Generic;
using System.Linq;

namespace UltimateMaintainer.Core
{
    // ---- what the rules need from Emby (Emby/EmbyPlayState.cs) ----

    public class UserRef
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    public class LibraryRef
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string CollectionType { get; set; }     // Emby's library type: movies, tvshows, empty for mixed, ...
    }

    // One user's play state of one item.
    public class PlayState
    {
        public long PositionTicks { get; set; }
        public bool Played { get; set; }
        public int PlayCount { get; set; }
    }

    public class PartialItem
    {
        public long ItemId { get; set; }
        public string Title { get; set; }
        public string LibraryId { get; set; }
        public string LibraryName { get; set; }
        public long? RunTimeTicks { get; set; }
        public DateTime? LastPlayedUtc { get; set; }
        public PlayState State { get; set; } = new PlayState();
    }

    public interface IPlayStates
    {
        List<UserRef> GetUsers();

        // Libraries with video in them (not collections or playlists, which only show the same items again).
        List<LibraryRef> GetLibraries();

        // The user's items with a resume point (PositionTicks > 0), played or not.
        List<PartialItem> GetPartials(string userId);

        // Current state, or null when the item or user is gone.
        PlayState Get(string userId, long itemId);

        void Save(string userId, long itemId, PlayState state);
    }

    // ---- settings ----

    public class PartialWatchSettings
    {
        public const string Watched = "watched";
        public const string Unwatched = "unwatched";

        public int Days { get; set; } = 14;
        public string Action { get; set; } = Watched;
        public Selection Users { get; set; } = Selection.Parse("all");
        public Selection Libraries { get; set; } = Selection.Parse("all");

        // Items with no last-played date count as stale too (otherwise they are skipped and listed).
        public bool IncludeUndated { get; set; }

        // Anything odd falls back to the safe reading: at least 1 day, unknown action = watched.
        public static PartialWatchSettings From(int days, string action, string users, string libraries, bool includeUndated = false) => new PartialWatchSettings
        {
            IncludeUndated = includeUndated,
            Days = Math.Max(1, days),
            Action = string.Equals(action?.Trim(), Unwatched, StringComparison.OrdinalIgnoreCase) ? Unwatched : Watched,
            Users = Selection.Parse(users),
            Libraries = Selection.Parse(libraries),
        };
    }

    // ---- plan and results ----

    public class PlannedChange
    {
        public string UserId { get; set; }
        public string UserName { get; set; }
        public long ItemId { get; set; }
        public string Title { get; set; }
        public string Library { get; set; }
        public int? Percent { get; set; }
        public int DaysIdle { get; set; }               // -1 = no last-played date (IncludeUndated)
        public PlayState Old { get; set; }
        public PlayState New { get; set; }

        // What the user will see afterwards, for Preview and the log.
        public string Result => New.Played && !Old.Played ? "marked watched" : "resume point cleared";
    }

    public class SkippedItem
    {
        public string UserName { get; set; }
        public string Title { get; set; }
        public string Reason { get; set; }
    }

    public class PartialWatchPlan
    {
        public List<PlannedChange> Changes { get; set; } = new List<PlannedChange>();
        public List<SkippedItem> Skipped { get; set; } = new List<SkippedItem>();
        public int Recent { get; set; }          // partial, but played within the timeout: left alone
        public int UsersChecked { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    // Saved after a run that changed something (last-run.json), so Undo knows what to put back.
    public class RunRecord
    {
        public DateTime DateUtc { get; set; }
        public string Action { get; set; }
        public int Days { get; set; }
        public bool Undone { get; set; }
        public List<PlannedChange> Changes { get; set; } = new List<PlannedChange>();
    }

    public class UndoResult
    {
        public int Restored { get; set; }
        public int Skipped { get; set; }
    }

    // ---- the rules ----

    public class PartialWatchCleaner
    {
        readonly IPlayStates _emby;
        readonly IRunLog _log;

        public PartialWatchCleaner(IPlayStates emby, IRunLog log)
        {
            _emby = emby;
            _log = log;
        }

        // Read only. One user failing (deleted halfway, Emby error) is reported and the others still count.
        public PartialWatchPlan Plan(PartialWatchSettings settings, DateTime nowUtc)
        {
            var plan = new PartialWatchPlan();
            foreach (var user in _emby.GetUsers().Where(u => settings.Users.Contains(u.Id)))
            {
                plan.UsersChecked++;
                List<PartialItem> items;
                try
                {
                    items = _emby.GetPartials(user.Id);
                }
                catch (Exception e)
                {
                    plan.Errors.Add($"{user.Name}: could not read play states: {e.Message}");
                    continue;
                }
                foreach (var item in items.Where(i => i.State.PositionTicks > 0 && settings.Libraries.Contains(i.LibraryId)))
                {
                    if (item.LastPlayedUtc == null && !settings.IncludeUndated)
                    {
                        plan.Skipped.Add(new SkippedItem { UserName = user.Name, Title = item.Title, Reason = "no last-played date" });
                        continue;
                    }
                    var days = item.LastPlayedUtc == null ? -1 : (int)Math.Floor((nowUtc - item.LastPlayedUtc.Value).TotalDays);
                    if (days >= 0 && days < settings.Days)
                    {
                        plan.Recent++;
                        continue;
                    }
                    plan.Changes.Add(new PlannedChange
                    {
                        UserId = user.Id,
                        UserName = user.Name,
                        ItemId = item.ItemId,
                        Title = item.Title,
                        Library = item.LibraryName,
                        Percent = Percent(item),
                        DaysIdle = days,
                        Old = Copy(item.State),
                        New = NewState(item.State, settings.Action),
                    });
                }
            }
            plan.Changes = plan.Changes.OrderByDescending(c => c.DaysIdle).ThenBy(c => c.UserName).ThenBy(c => c.Title).ToList();
            return plan;
        }

        static int? Percent(PartialItem item) =>
            item.RunTimeTicks > 0 ? (int?)Math.Min(100, (int)Math.Round(item.State.PositionTicks * 100.0 / item.RunTimeTicks.Value)) : null;

        static PlayState Copy(PlayState s) => new PlayState { PositionTicks = s.PositionTicks, Played = s.Played, PlayCount = s.PlayCount };

        // Watched: like Emby's own "Mark played" (played, one more play counted). Unwatched: only the resume point
        // goes. Either way an item that was already played stays played (see the NOTE at the top).
        internal static PlayState NewState(PlayState old, string action)
        {
            var markPlayed = action == PartialWatchSettings.Watched && !old.Played;
            return new PlayState
            {
                PositionTicks = 0,
                Played = old.Played || markPlayed,
                PlayCount = markPlayed ? old.PlayCount + 1 : old.PlayCount,
            };
        }

        static bool Same(PlayState a, PlayState b) =>
            a != null && b != null && a.PositionTicks == b.PositionTicks && a.Played == b.Played && a.PlayCount == b.PlayCount;

        // Changes what the plan found. Each item is read again first: if the user played it since the plan was made
        // (Preview, then Run later), it is left alone. A failure on one item is logged and the rest go on.
        public RunRecord Apply(PartialWatchPlan plan, PartialWatchSettings settings, DateTime nowUtc)
        {
            var record = new RunRecord { DateUtc = nowUtc, Action = settings.Action, Days = settings.Days };
            foreach (var c in plan.Changes)
            {
                try
                {
                    if (!Same(_emby.Get(c.UserId, c.ItemId), c.Old))
                    {
                        _log.Info($"{c.UserName}: {c.Title}: changed since it was checked, left alone");
                        continue;
                    }
                    _emby.Save(c.UserId, c.ItemId, c.New);
                    record.Changes.Add(c);
                    _log.Info($"{c.UserName}: {c.Title} ({PercentText(c.Percent)}, {IdleText(c.DaysIdle)}): {c.Result}");
                }
                catch (Exception e)
                {
                    _log.Warn($"{c.UserName}: {c.Title}: failed, left as it was: {e.Message}");
                }
            }
            return record;
        }

        internal static string IdleText(int days) => days < 0 ? "no play date" : $"last played {days} days ago";

        internal static string PercentText(int? percent) => percent == null ? "?%" : percent + "%";

        // Puts back what a run changed, but only where the item is still exactly as the run left it: if the user has
        // played it since, their newer state wins.
        public UndoResult Undo(RunRecord record)
        {
            var result = new UndoResult();
            foreach (var c in record.Changes)
            {
                try
                {
                    if (!Same(_emby.Get(c.UserId, c.ItemId), c.New))
                    {
                        result.Skipped++;
                        _log.Info($"undo {c.UserName}: {c.Title}: changed since the run, left alone");
                        continue;
                    }
                    _emby.Save(c.UserId, c.ItemId, c.Old);
                    result.Restored++;
                    _log.Info($"undo {c.UserName}: {c.Title}: resume point put back");
                }
                catch (Exception e)
                {
                    result.Skipped++;
                    _log.Warn($"undo {c.UserName}: {c.Title}: failed: {e.Message}");
                }
            }
            record.Undone = true;
            return result;
        }
    }
}
