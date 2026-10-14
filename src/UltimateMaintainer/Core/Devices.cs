// Devices.cs
//
// Old devices cleanup: Emby keeps a device entry (Dashboard > Devices) for every browser, phone and TV that ever
// signed in, forever. On a typical server most of them have not been seen for months, mostly old web-browser logins.
// Devices not seen for N days are removed, which signs that login out; if the device comes back, the user signs in
// again.
//
// Plan() only looks (Preview); Apply() removes and returns a DeviceRunRecord (kept for looking back).
//
// NOTE: there is no undo: Emby drops the device's sign-in with it. So a device that is in use right now (an open
// session) is never removed, whatever its date, and each device is checked again just before removing it.

using System;
using System.Collections.Generic;
using System.Linq;

namespace UltimateMaintainer.Core
{
    public class DeviceEntry
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string App { get; set; }
        public string UserId { get; set; }              // null when Emby does not know who last used it
        public string UserName { get; set; }
        public DateTime? LastActivityUtc { get; set; }
        public bool InUse { get; set; }                 // has an open session right now
    }

    public interface IDevices
    {
        List<DeviceEntry> GetDevices();

        void Delete(long[] ids);
    }

    public class DeviceSettings
    {
        public int Days { get; set; } = 90;
        public Selection Users { get; set; } = Selection.Parse("all");

        // At least 7 days: a shorter timeout would sign out people who just go on holiday.
        public static DeviceSettings From(int days, string users) => new DeviceSettings
        {
            Days = Math.Max(7, days),
            Users = Selection.Parse(users),
        };
    }

    public class DeviceChange
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string App { get; set; }
        public string UserName { get; set; }
        public int DaysIdle { get; set; }
    }

    public class DevicePlan
    {
        public List<DeviceChange> Changes { get; set; } = new List<DeviceChange>();
        public List<SkippedItem> Skipped { get; set; } = new List<SkippedItem>();
        public int Recent { get; set; }
        public int Total { get; set; }
    }

    public class DeviceRunRecord
    {
        public DateTime DateUtc { get; set; }
        public int Days { get; set; }
        public List<DeviceChange> Changes { get; set; } = new List<DeviceChange>();
    }

    public class DeviceCleaner
    {
        readonly IDevices _emby;
        readonly IRunLog _log;

        public DeviceCleaner(IDevices emby, IRunLog log)
        {
            _emby = emby;
            _log = log;
        }

        // With "only these users", a device nobody is known to have used is left alone: it belongs to no chosen user.
        public DevicePlan Plan(DeviceSettings settings, DateTime nowUtc)
        {
            var plan = new DevicePlan();
            foreach (var d in _emby.GetDevices())
            {
                plan.Total++;
                if (!settings.Users.All && (d.UserId == null || !settings.Users.Contains(d.UserId)))
                    continue;
                if (d.InUse)
                {
                    plan.Recent++;
                    continue;
                }
                if (d.LastActivityUtc == null)
                {
                    plan.Skipped.Add(new SkippedItem { UserName = d.UserName ?? "?", Title = $"{d.Name} ({d.App})", Reason = "no last-seen date" });
                    continue;
                }
                var days = (int)Math.Floor((nowUtc - d.LastActivityUtc.Value).TotalDays);
                if (days < settings.Days)
                {
                    plan.Recent++;
                    continue;
                }
                plan.Changes.Add(new DeviceChange { Id = d.Id, Name = d.Name, App = d.App, UserName = d.UserName ?? "", DaysIdle = days });
            }
            plan.Changes = plan.Changes.OrderByDescending(c => c.DaysIdle).ThenBy(c => c.UserName).ToList();
            return plan;
        }

        // Read the devices again and remove only those still exactly as the plan saw them: not in use, not seen
        // since. One Delete call for all of them, in batches so a failure costs at most one batch.
        public DeviceRunRecord Apply(DevicePlan plan, DeviceSettings settings, DateTime nowUtc)
        {
            var record = new DeviceRunRecord { DateUtc = nowUtc, Days = settings.Days };
            var current = _emby.GetDevices().ToDictionary(d => d.Id);
            var go = new List<DeviceChange>();
            foreach (var c in plan.Changes)
            {
                if (!current.TryGetValue(c.Id, out var d) || d.InUse || d.LastActivityUtc == null
                    || (nowUtc - d.LastActivityUtc.Value).TotalDays < settings.Days)
                {
                    _log.Info($"device {c.Name} ({c.App}, {c.UserName}): changed since it was checked, left alone");
                    continue;
                }
                go.Add(c);
            }
            foreach (var batch in go.Select((c, i) => (c, i)).GroupBy(x => x.i / 50, x => x.c))
            {
                var list = batch.ToList();
                try
                {
                    _emby.Delete(list.Select(c => c.Id).ToArray());
                    record.Changes.AddRange(list);
                    foreach (var c in list)
                        _log.Info($"device {c.Name} ({c.App}, {(c.UserName.Length > 0 ? c.UserName : "no user")}, last seen {c.DaysIdle} days ago): removed");
                }
                catch (Exception e)
                {
                    _log.Warn($"removing {list.Count} device(s) failed, left as they were: {e.Message}");
                }
            }
            return record;
        }
    }
}
