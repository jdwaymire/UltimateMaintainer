// DevicesApi.cs
//
// REST routes behind the settings page's "Old devices cleanup" section: Preview (the page's unsaved values, changes
// nothing), Run now (saved settings) and the last-run line. No Undo route: see Core/Devices.cs.
//
// NOTE: admin only. Run is refused straight away while another job holds WorkLock.

using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;
using UltimateMaintainer.Core;

namespace UltimateMaintainer.Emby
{
    [Route("/UltimateMaintainer/Devices/Preview", "POST", Summary = "Which devices a cleanup with these settings would remove (changes nothing)")]
    [Authenticated(Roles = "Admin")]
    public class DevicesPreviewRequest : IReturn<DevicesPreview>
    {
        public int Days { get; set; }
        public string Users { get; set; }
    }

    public class DevicesPreview
    {
        public List<DeviceChange> Rows { get; set; } = new List<DeviceChange>();
        public List<SkippedItem> Skipped { get; set; } = new List<SkippedItem>();
        public int Recent { get; set; }
        public int Total { get; set; }
    }

    [Route("/UltimateMaintainer/Devices/Run", "POST", Summary = "Run the old devices cleanup now with the saved settings")]
    [Authenticated(Roles = "Admin")]
    public class DevicesRunRequest : IReturn<LastRunView>
    {
    }

    [Route("/UltimateMaintainer/Devices/LastRun", "GET", Summary = "The last old devices cleanup that removed something")]
    [Authenticated(Roles = "Admin")]
    public class DevicesLastRunRequest : IReturn<LastRunView>
    {
    }

    public class DevicesApi : IService
    {
        readonly DevicesJob _job;

        public DevicesApi(IDeviceManager devices, ISessionManager sessions, IUserManager users, ILogManager logManager,
            IJsonSerializer jsonSerializer)
        {
            _job = new DevicesJob(devices, sessions, users, logManager, jsonSerializer);
        }

        public object Post(DevicesPreviewRequest request)
        {
            var plan = _job.Preview(DeviceSettings.From(request.Days, request.Users));
            return new DevicesPreview { Rows = plan.Changes, Skipped = plan.Skipped, Recent = plan.Recent, Total = plan.Total };
        }

        public object Post(DevicesRunRequest request)
        {
            if (!Plugin.WorkLock.Wait(0))
                throw new InvalidOperationException("A cleanup is running right now. Try again when it has finished.");
            try
            {
                _job.Run(manual: true);
            }
            finally
            {
                Plugin.WorkLock.Release();
            }
            return Get(new DevicesLastRunRequest());
        }

        public object Get(DevicesLastRunRequest request)
        {
            var r = _job.LastRun();
            return (r == null
                ? new LastRunView()
                : new LastRunView
                {
                    Exists = true,
                    Date = r.DateUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    Changed = r.Changes.Count,
                    Action = "removed",
                    Undone = false,
                }).With(_job.Latest());
        }
    }
}
