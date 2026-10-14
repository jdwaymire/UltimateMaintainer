// NextUpTask.cs
//
// The scheduled task "Ultimate Maintainer > Next episode cleanup", daily at 04:05 (after the partial-watch cleanup,
// whose "mark watched" can bring up new next episodes). Also NextUpJob, the run itself, shared with the settings
// page's "Run now" and "Undo" (NextUpApi).
//
// NOTE: like the partial-watch cleanup, the scheduled run does nothing while switched off, "Run now" runs anyway, and
// next-up/last-run.json is only replaced by a run that hid something, so a quiet daily run keeps the last Undo.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using UltimateMaintainer.Core;

namespace UltimateMaintainer.Emby
{
    public class NextUpTask : IScheduledTask
    {
        readonly NextUpJob _job;

        public NextUpTask(ILibraryManager library, IUserManager users, IUserDataManager userData, ITVSeriesManager tv,
            ILogManager logManager, IJsonSerializer jsonSerializer)
        {
            _job = new NextUpJob(library, users, userData, tv, logManager, jsonSerializer);
        }

        public string Name => "Next episode cleanup";
        public string Key => "UltimateMaintainerNextUp";
        public string Description => "Hides the next-episode entries of series nobody has played for a while from Continue Watching (Ultimate Maintainer settings).";
        public string Category => "Ultimate Maintainer";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerDaily, TimeOfDayTicks = TimeSpan.FromHours(4).Add(TimeSpan.FromMinutes(5)).Ticks },
        };

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            await Plugin.WorkLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!Plugin.Instance.Configuration.NextUpEnabled)
                {
                    using (var log = _job.OpenLog())
                        log.Info("next episode cleanup is switched off in the settings; nothing done");
                    return;
                }
                _job.Run();
                progress.Report(100);
            }
            finally
            {
                Plugin.WorkLock.Release();
            }
        }
    }

    // One run, an undo, or a preview. Callers hold WorkLock for Run and Undo.
    public class NextUpJob
    {
        readonly INextUps _emby;
        readonly ILogger _logger;
        readonly EmbyJson _json;

        public NextUpJob(ILibraryManager library, IUserManager users, IUserDataManager userData, ITVSeriesManager tv,
            ILogManager logManager, IJsonSerializer jsonSerializer)
        {
            _emby = new EmbyNextUps(library, users, userData, tv);
            _logger = logManager.GetLogger("UltimateMaintainer");
            _json = new EmbyJson(jsonSerializer);
        }

        static string DataDir => Plugin.Instance.DataFolderPath;
        static string JobDir => Path.Combine(DataDir, "next-up");
        static string LastRunPath => Path.Combine(JobDir, "last-run.json");
        static string LatestRunPath => Path.Combine(JobDir, "latest-run.json");

        public RunLog OpenLog() => new RunLog(_logger, Path.Combine(DataDir, "logs"));

        public NextUpPlan Preview(NextUpSettings settings) =>
            new NextUpCleaner(_emby, new NullLog()).Plan(settings, DateTime.UtcNow);

        // With the SAVED settings. Every run is also kept in next-up/runs/ (90 days).
        public NextUpRunRecord Run(bool manual = false)
        {
            var settings = Plugin.Instance.Configuration.GetNextUpSettings();
            using (var log = OpenLog())
            {
                log.Info($"next episode cleanup: {settings.Days} days, users {settings.Users}, libraries {settings.Libraries}");
                var cleaner = new NextUpCleaner(_emby, log);
                var now = DateTime.UtcNow;
                var plan = cleaner.Plan(settings, now);
                foreach (var e in plan.Errors)
                    log.Warn(e);
                var record = cleaner.Apply(plan, settings, now);
                if (record.Changes.Count > 0)
                {
                    Directory.CreateDirectory(Path.Combine(JobDir, "runs"));
                    JsonFiles.Save(_json, LastRunPath, record);
                    JsonFiles.Save(_json, Path.Combine(JobDir, "runs", now.ToLocalTime().ToString("yyyy-MM-dd_HHmmss") + ".json"), record);
                    JsonFiles.Prune(Path.Combine(JobDir, "runs"), 90);
                }
                JsonFiles.Save(_json, LatestRunPath, new LatestRun { DateUtc = now, Changed = record.Changes.Count, Action = "hidden", Recent = plan.Recent, Skipped = plan.Skipped.Count, Errors = plan.Errors.Count });
                JsonFiles.AddHistory(_json, log, new HistoryEntry { DateUtc = now, Job = "next-up", Kind = manual ? RunHistory.Manual : RunHistory.Scheduled, Changed = record.Changes.Count, Action = "hidden", Recent = plan.Recent, Skipped = plan.Skipped.Count, Errors = plan.Errors.Count });
                log.Info($"next episode cleanup done: {record.Changes.Count} hidden, {plan.Changes.Count - record.Changes.Count} left alone, " +
                         $"{plan.Recent} played recently, {plan.Skipped.Count} without a play date, {plan.UsersChecked} user(s), {plan.Errors.Count} error(s)");
                return record;
            }
        }

        public NextUpRunRecord LastRun() => JsonFiles.Load<NextUpRunRecord>(_json, LastRunPath);

        public LatestRun Latest() => JsonFiles.Load<LatestRun>(_json, LatestRunPath);

        public UndoResult Undo()
        {
            var record = LastRun();
            if (record == null || record.Undone || record.Changes.Count == 0)
                throw new InvalidOperationException("There is no run to undo.");
            using (var log = OpenLog())
            {
                log.Info($"undo of the next episode cleanup from {record.DateUtc.ToLocalTime():yyyy-MM-dd HH:mm} ({record.Changes.Count} entries)");
                var result = new NextUpCleaner(_emby, log).Undo(record);
                JsonFiles.AddHistory(_json, log, new HistoryEntry { DateUtc = DateTime.UtcNow, Job = "next-up", Kind = RunHistory.Undo, Changed = result.Restored, Skipped = result.Skipped });
                JsonFiles.Save(_json, LastRunPath, record);
                log.Info($"undo done: {result.Restored} back, {result.Skipped} failed");
                return result;
            }
        }
    }
}
