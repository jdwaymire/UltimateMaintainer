// DevicesTask.cs
//
// The scheduled task "Ultimate Maintainer > Old devices cleanup", daily at 04:10 (after the two Continue Watching
// jobs). Also DevicesJob, the run itself, shared with the settings page's "Run now" (DevicesApi).
//
// NOTE: no Undo for this job (Emby drops the sign-in with the device); every run that removed something is still
// kept in devices/runs/ (90 days), so the log can say what went. The scheduled run does nothing while switched off.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using UltimateMaintainer.Core;

namespace UltimateMaintainer.Emby
{
    public class DevicesTask : IScheduledTask
    {
        readonly DevicesJob _job;

        public DevicesTask(IDeviceManager devices, ISessionManager sessions, IUserManager users, ILogManager logManager,
            IJsonSerializer jsonSerializer)
        {
            _job = new DevicesJob(devices, sessions, users, logManager, jsonSerializer);
        }

        public string Name => "Old devices cleanup";
        public string Key => "UltimateMaintainerDevices";
        public string Description => "Removes devices not seen for a long time from Dashboard > Devices (Ultimate Maintainer settings).";
        public string Category => "Ultimate Maintainer";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerDaily, TimeOfDayTicks = TimeSpan.FromHours(4).Add(TimeSpan.FromMinutes(10)).Ticks },
        };

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            await Plugin.WorkLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!Plugin.Instance.Configuration.DevicesEnabled)
                {
                    using (var log = _job.OpenLog())
                        log.Info("old devices cleanup is switched off in the settings; nothing done");
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

    public class DevicesJob
    {
        readonly IDevices _emby;
        readonly ILogger _logger;
        readonly EmbyJson _json;

        public DevicesJob(IDeviceManager devices, ISessionManager sessions, IUserManager users, ILogManager logManager,
            IJsonSerializer jsonSerializer)
        {
            _emby = new EmbyDevices(devices, sessions, users);
            _logger = logManager.GetLogger("UltimateMaintainer");
            _json = new EmbyJson(jsonSerializer);
        }

        static string DataDir => Plugin.Instance.DataFolderPath;
        static string JobDir => Path.Combine(DataDir, "devices");
        static string LastRunPath => Path.Combine(JobDir, "last-run.json");
        static string LatestRunPath => Path.Combine(JobDir, "latest-run.json");

        public RunLog OpenLog() => new RunLog(_logger, Path.Combine(DataDir, "logs"));

        public DevicePlan Preview(DeviceSettings settings) =>
            new DeviceCleaner(_emby, new NullLog()).Plan(settings, DateTime.UtcNow);

        public DeviceRunRecord Run()
        {
            var settings = Plugin.Instance.Configuration.GetDeviceSettings();
            using (var log = OpenLog())
            {
                log.Info($"old devices cleanup: {settings.Days} days, users {settings.Users}");
                var cleaner = new DeviceCleaner(_emby, log);
                var now = DateTime.UtcNow;
                var plan = cleaner.Plan(settings, now);
                var record = cleaner.Apply(plan, settings, now);
                if (record.Changes.Count > 0)
                {
                    Directory.CreateDirectory(Path.Combine(JobDir, "runs"));
                    JsonFiles.Save(_json, LastRunPath, record);
                    JsonFiles.Save(_json, Path.Combine(JobDir, "runs", now.ToLocalTime().ToString("yyyy-MM-dd_HHmmss") + ".json"), record);
                    JsonFiles.Prune(Path.Combine(JobDir, "runs"), 90);
                }
                JsonFiles.Save(_json, LatestRunPath, new LatestRun { DateUtc = now, Changed = record.Changes.Count, Action = "removed", Recent = plan.Recent, Skipped = plan.Skipped.Count });
                log.Info($"old devices cleanup done: {record.Changes.Count} removed, {plan.Changes.Count - record.Changes.Count} left alone, " +
                         $"{plan.Recent} seen recently or in use, {plan.Skipped.Count} without a date, {plan.Total} devices in all");
                return record;
            }
        }

        public DeviceRunRecord LastRun() => JsonFiles.Load<DeviceRunRecord>(_json, LastRunPath);

        public LatestRun Latest() => JsonFiles.Load<LatestRun>(_json, LatestRunPath);
    }
}
