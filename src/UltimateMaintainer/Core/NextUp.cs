// NextUp.cs
//
// Next-episode cleanup: Emby's Continue Watching also offers the next unplayed episode of every series a user has
// started (a "next up" entry, 0%, no resume point). Shows dropped years ago stay there for good. Once a series has
// not been played for N days, its entry is hidden with Emby's own "hide from Continue Watching". Nothing is marked
// watched; the user still finds the show in the library.
//
// Plan() only looks (Preview); Apply() hides and returns a NextUpRunRecord, which Undo() unhides again.
//
// NOTE: "not played for N days" is the series' last activity: the newest last-played date of any of its episodes for
// that user, not the next episode's own date (it usually has none). Entries without any play date are skipped and
// listed. Checked on a real server 2026-10-12: hiding the shown episode removes the series' entry and no other episode of
// it takes its place; unhiding brings it back.

using System;
using System.Collections.Generic;
using System.Linq;

namespace UltimateMaintainer.Core
{
    // One next-up entry of one user.
    public class NextUpItem
    {
        public long EpisodeId { get; set; }
        public string Series { get; set; }
        public string Episode { get; set; }             // "S3E20 Tin Man"
        public string LibraryId { get; set; }
        public string LibraryName { get; set; }
        public DateTime? SeriesLastPlayedUtc { get; set; }

        // The user has a partly watched episode of this series. Continue Watching then shows that episode, not this
        // one, and hiding the series could hide the partly watched episode too (that is the partial-watch cleanup's
        // job). Found 2026-10-12: GetNextUp still lists these.
        public bool SeriesHasResumePoint { get; set; }
    }

    public interface INextUps
    {
        List<UserRef> GetUsers();

        // The user's next-up entries as Continue Watching shows them (hidden ones are not returned).
        List<NextUpItem> GetNextUps(string userId);

        void SetHidden(string userId, long episodeId, bool hidden);
    }

    public class NextUpSettings
    {
        public int Days { get; set; } = 14;
        public Selection Users { get; set; } = Selection.Parse("all");
        public Selection Libraries { get; set; } = Selection.Parse("all");

        public static NextUpSettings From(int days, string users, string libraries) => new NextUpSettings
        {
            Days = Math.Max(1, days),
            Users = Selection.Parse(users),
            Libraries = Selection.Parse(libraries),
        };
    }

    public class NextUpChange
    {
        public string UserId { get; set; }
        public string UserName { get; set; }
        public long EpisodeId { get; set; }
        public string Series { get; set; }
        public string Episode { get; set; }
        public string Library { get; set; }
        public int DaysIdle { get; set; }
    }

    public class NextUpPlan
    {
        public List<NextUpChange> Changes { get; set; } = new List<NextUpChange>();
        public List<SkippedItem> Skipped { get; set; } = new List<SkippedItem>();
        public int Recent { get; set; }
        public int UsersChecked { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    public class NextUpRunRecord
    {
        public DateTime DateUtc { get; set; }
        public int Days { get; set; }
        public bool Undone { get; set; }
        public List<NextUpChange> Changes { get; set; } = new List<NextUpChange>();
    }

    public class NextUpCleaner
    {
        readonly INextUps _emby;
        readonly IRunLog _log;

        public NextUpCleaner(INextUps emby, IRunLog log)
        {
            _emby = emby;
            _log = log;
        }

        // Read only. One user failing is reported and the others still count.
        public NextUpPlan Plan(NextUpSettings settings, DateTime nowUtc)
        {
            var plan = new NextUpPlan();
            foreach (var user in _emby.GetUsers().Where(u => settings.Users.Contains(u.Id)))
            {
                plan.UsersChecked++;
                List<NextUpItem> items;
                try
                {
                    items = _emby.GetNextUps(user.Id);
                }
                catch (Exception e)
                {
                    plan.Errors.Add($"{user.Name}: could not read next episodes: {e.Message}");
                    continue;
                }
                foreach (var item in items.Where(i => settings.Libraries.Contains(i.LibraryId) && !i.SeriesHasResumePoint))
                {
                    if (item.SeriesLastPlayedUtc == null)
                    {
                        plan.Skipped.Add(new SkippedItem { UserName = user.Name, Title = $"{item.Series} {item.Episode}", Reason = "no play date in the series" });
                        continue;
                    }
                    var days = (int)Math.Floor((nowUtc - item.SeriesLastPlayedUtc.Value).TotalDays);
                    if (days < settings.Days)
                    {
                        plan.Recent++;
                        continue;
                    }
                    plan.Changes.Add(new NextUpChange
                    {
                        UserId = user.Id,
                        UserName = user.Name,
                        EpisodeId = item.EpisodeId,
                        Series = item.Series,
                        Episode = item.Episode,
                        Library = item.LibraryName,
                        DaysIdle = days,
                    });
                }
            }
            plan.Changes = plan.Changes.OrderByDescending(c => c.DaysIdle).ThenBy(c => c.UserName).ThenBy(c => c.Series).ToList();
            return plan;
        }

        // Hides what the plan found. Each user's entries are read again first: an entry that is gone or now shows a
        // different episode (the user played on since the preview) is left alone.
        public NextUpRunRecord Apply(NextUpPlan plan, NextUpSettings settings, DateTime nowUtc)
        {
            var record = new NextUpRunRecord { DateUtc = nowUtc, Days = settings.Days };
            var current = new Dictionary<string, HashSet<long>>();
            foreach (var c in plan.Changes)
            {
                try
                {
                    if (!current.TryGetValue(c.UserId, out var shown))
                        current[c.UserId] = shown = new HashSet<long>(_emby.GetNextUps(c.UserId).Select(i => i.EpisodeId));
                    if (!shown.Contains(c.EpisodeId))
                    {
                        _log.Info($"{c.UserName}: {c.Series} {c.Episode}: changed since it was checked, left alone");
                        continue;
                    }
                    _emby.SetHidden(c.UserId, c.EpisodeId, true);
                    record.Changes.Add(c);
                    _log.Info($"{c.UserName}: {c.Series} {c.Episode} (series last played {c.DaysIdle} days ago): hidden from Continue Watching");
                }
                catch (Exception e)
                {
                    _log.Warn($"{c.UserName}: {c.Series} {c.Episode}: failed, left as it was: {e.Message}");
                }
            }
            return record;
        }

        // Unhides every entry the run hid. Harmless where the user has played the series since (Emby unhid it then).
        public UndoResult Undo(NextUpRunRecord record)
        {
            var result = new UndoResult();
            foreach (var c in record.Changes)
            {
                try
                {
                    _emby.SetHidden(c.UserId, c.EpisodeId, false);
                    result.Restored++;
                    _log.Info($"undo {c.UserName}: {c.Series} {c.Episode}: back in Continue Watching");
                }
                catch (Exception e)
                {
                    result.Skipped++;
                    _log.Warn($"undo {c.UserName}: {c.Series} {c.Episode}: failed: {e.Message}");
                }
            }
            record.Undone = true;
            return result;
        }
    }
}
