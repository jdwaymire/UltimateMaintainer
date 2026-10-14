// PartialWatchTask.cs
//
// The scheduled task "Ultimate Maintainer > Partial watch cleanup" (Dashboard > Scheduled Tasks), daily at 04:00 so
// it is done before Ultimate Collections starts at 05:00. Also PartialWatchJob, the run itself, shared with the
// settings page's "Run now" and "Undo" (PartialWatchApi), and the small adapters every job needs: EmbyJson, RunLog,
// JsonFiles.
//
// NOTE: the scheduled run does nothing while the cleanup is switched off; "Run now" on the page runs it anyway (the
// admin pressed it on purpose, after a preview). last-run.json is only replaced by a run that changed something, so
// a quiet daily run never takes away the Undo of the last real one.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using UltimateMaintainer.Core;

namespace UltimateMaintainer.Emby
{
    public class PartialWatchTask : IScheduledTask
    {
        readonly PartialWatchJob _job;

        public PartialWatchTask(ILibraryManager library, IUserManager users, IUserDataManager userData, ILogManager logManager,
            IJsonSerializer jsonSerializer)
        {
            _job = new PartialWatchJob(library, users, userData, logManager, jsonSerializer);
        }

        public string Name => "Partial watch cleanup";
        public string Key => "UltimateMaintainerPartialWatch";
        public string Description => "Marks items left partly watched for too long as watched, or clears their resume point (Ultimate Maintainer settings).";
        public string Category => "Ultimate Maintainer";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerDaily, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks },
        };

        // Waits for WorkLock, so a "Run now" or "Undo" from the page finishes first.
        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            await Plugin.WorkLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!Plugin.Instance.Configuration.PartialWatchEnabled)
                {
                    using (var log = _job.OpenLog())
                        log.Info("partial watch cleanup is switched off in the settings; nothing done");
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

    // One cleanup run, an undo, or a preview. Callers hold WorkLock for Run and Undo.
    public class PartialWatchJob
    {
        readonly IPlayStates _emby;
        readonly ILogger _logger;
        readonly EmbyJson _json;

        public PartialWatchJob(ILibraryManager library, IUserManager users, IUserDataManager userData, ILogManager logManager,
            IJsonSerializer jsonSerializer)
        {
            _emby = new EmbyPlayState(library, users, userData);
            _logger = logManager.GetLogger("UltimateMaintainer");
            _json = new EmbyJson(jsonSerializer);
        }

        public IPlayStates Emby => _emby;

        static string DataDir => Plugin.Instance.DataFolderPath;
        static string JobDir => Path.Combine(DataDir, "partial-watch");
        static string LastRunPath => Path.Combine(JobDir, "last-run.json");
        static string LatestRunPath => Path.Combine(JobDir, "latest-run.json");

        public RunLog OpenLog() => new RunLog(_logger, Path.Combine(DataDir, "logs"));

        public PartialWatchPlan Preview(PartialWatchSettings settings) =>
            new PartialWatchCleaner(_emby, new NullLog()).Plan(settings, DateTime.UtcNow);

        // With the SAVED settings. Every run is also kept in partial-watch/runs/ (90 days) for looking back.
        public RunRecord Run()
        {
            var settings = Plugin.Instance.Configuration.GetPartialWatchSettings();
            using (var log = OpenLog())
            {
                log.Info($"partial watch cleanup: {settings.Days} days, action {settings.Action}, users {settings.Users}, libraries {settings.Libraries}{(settings.IncludeUndated ? ", items without a play date too" : "")}");
                var cleaner = new PartialWatchCleaner(_emby, log);
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
                JsonFiles.Save(_json, LatestRunPath, new LatestRun { DateUtc = now, Changed = record.Changes.Count, Action = settings.Action, Recent = plan.Recent, Skipped = plan.Skipped.Count, Errors = plan.Errors.Count });
                log.Info($"partial watch cleanup done: {record.Changes.Count} changed, {plan.Changes.Count - record.Changes.Count} left alone, " +
                         $"{plan.Recent} played recently, {plan.Skipped.Count} without a play date, {plan.UsersChecked} user(s), {plan.Errors.Count} error(s)");
                return record;
            }
        }

        public RunRecord LastRun() => JsonFiles.Load<RunRecord>(_json, LastRunPath);

        public LatestRun Latest() => JsonFiles.Load<LatestRun>(_json, LatestRunPath);

        // The last run that changed something, once. Refused when there is nothing to undo.
        public UndoResult Undo()
        {
            var record = LastRun();
            if (record == null || record.Undone || record.Changes.Count == 0)
                throw new InvalidOperationException("There is no run to undo.");
            using (var log = OpenLog())
            {
                log.Info($"undo of the partial watch cleanup from {record.DateUtc.ToLocalTime():yyyy-MM-dd HH:mm} ({record.Changes.Count} items)");
                var result = new PartialWatchCleaner(_emby, log).Undo(record);
                JsonFiles.Save(_json, LastRunPath, record);
                log.Info($"undo done: {result.Restored} put back, {result.Skipped} left alone");
                return result;
            }
        }
    }

    // ---- adapters ----

    // IJson on Emby's own serializer, so the plugin ships no JSON library (deploy stays one DLL).
    public class EmbyJson : IJson
    {
        readonly IJsonSerializer _s;

        public EmbyJson(IJsonSerializer s) => _s = s;

        public T Deserialize<T>(string json) => _s.DeserializeFromString<T>(json);

        public string Serialize(object value) => _s.SerializeToString(value);
    }

    public static class JsonFiles
    {
        // A missing file gives null.
        public static T Load<T>(IJson json, string path) where T : class =>
            File.Exists(path) ? json.Deserialize<T>(File.ReadAllText(path)) : null;

        // Write to a .tmp file and swap it in, so a crash halfway through never leaves a half-written file (losing
        // last-run.json would lose the Undo).
        public static void Save(IJson json, string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json.Serialize(value));
            if (File.Exists(path))
                File.Replace(tmp, path, null);
            else
                File.Move(tmp, path);
        }

        public static void Prune(string dir, int keepDays)
        {
            foreach (var f in Directory.GetFiles(dir))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-keepDays))
                    File.Delete(f);
        }
    }

    // Logs to Emby's server log and to a daily file in the plugin's data folder (kept 30 days).
    public sealed class RunLog : IRunLog, IDisposable
    {
        const int KeepDays = 30;
        readonly ILogger _logger;
        readonly StreamWriter _file;

        public RunLog(ILogger logger, string dir)
        {
            _logger = logger;
            Directory.CreateDirectory(dir);
            JsonFiles.Prune(dir, KeepDays);
            _file = new StreamWriter(Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd") + ".log"), append: true) { AutoFlush = true };
        }

        void Write(string level, string message) =>
            _file.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {(level == "INFO" ? "" : level + " ")}{message}");

        // NOTE: the message goes in as an argument to "{0}", never as the format string itself. Emby's ILogger
        // formats its first argument, so a title with { or } in it would break or garble the log line.
        public void Info(string message)
        {
            _logger.Info("{0}", message);
            Write("INFO", message);
        }

        public void Warn(string message)
        {
            _logger.Warn("{0}", message);
            Write("WARN", message);
        }

        public void Error(string message)
        {
            _logger.Error("{0}", message);
            Write("ERROR", message);
        }

        public void Dispose() => _file.Dispose();
    }

    // Preview logs nothing.
    class NullLog : IRunLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }
}
