// NextUpApi.cs
//
// REST routes behind the settings page's "Next episode cleanup" section: Preview (with the page's unsaved values,
// changes nothing), Run now and Undo (with the saved settings), and the last-run line.
//
// NOTE: admin only, like PartialWatchApi. Run and Undo are refused straight away while another job holds WorkLock.

using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;
using UltimateMaintainer.Core;

namespace UltimateMaintainer.Emby
{
    [Route("/UltimateMaintainer/NextUp/Preview", "POST", Summary = "Which next-episode entries a cleanup with these settings would hide (changes nothing)")]
    [Authenticated(Roles = "Admin")]
    public class NextUpPreviewRequest : IReturn<NextUpPreview>
    {
        public int Days { get; set; }
        public string Users { get; set; }
        public string Libraries { get; set; }
    }

    public class NextUpPreview
    {
        public List<NextUpChange> Rows { get; set; } = new List<NextUpChange>();
        public List<SkippedItem> Skipped { get; set; } = new List<SkippedItem>();
        public int Recent { get; set; }
        public int UsersChecked { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    [Route("/UltimateMaintainer/NextUp/Run", "POST", Summary = "Run the next episode cleanup now with the saved settings")]
    [Authenticated(Roles = "Admin")]
    public class NextUpRunRequest : IReturn<LastRunView>
    {
    }

    [Route("/UltimateMaintainer/NextUp/LastRun", "GET", Summary = "The last next episode cleanup that hid something")]
    [Authenticated(Roles = "Admin")]
    public class NextUpLastRunRequest : IReturn<LastRunView>
    {
    }

    [Route("/UltimateMaintainer/NextUp/Undo", "POST", Summary = "Bring back what the last next episode cleanup hid")]
    [Authenticated(Roles = "Admin")]
    public class NextUpUndoRequest : IReturn<UndoResult>
    {
    }

    public class NextUpApi : IService
    {
        readonly NextUpJob _job;

        public NextUpApi(ILibraryManager library, IUserManager users, IUserDataManager userData, ITVSeriesManager tv,
            ILogManager logManager, IJsonSerializer jsonSerializer)
        {
            _job = new NextUpJob(library, users, userData, tv, logManager, jsonSerializer);
        }

        public object Post(NextUpPreviewRequest request)
        {
            var plan = _job.Preview(NextUpSettings.From(request.Days, request.Users, request.Libraries));
            return new NextUpPreview
            {
                Rows = plan.Changes,
                Skipped = plan.Skipped,
                Recent = plan.Recent,
                UsersChecked = plan.UsersChecked,
                Errors = plan.Errors,
            };
        }

        public object Post(NextUpRunRequest request)
        {
            Locked(() => _job.Run(manual: true));
            return Get(new NextUpLastRunRequest());
        }

        public object Get(NextUpLastRunRequest request)
        {
            var r = _job.LastRun();
            return (r == null
                ? new LastRunView()
                : new LastRunView
                {
                    Exists = true,
                    Date = r.DateUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    Changed = r.Changes.Count,
                    Action = "hidden",
                    Undone = r.Undone,
                }).With(_job.Latest());
        }

        public object Post(NextUpUndoRequest request)
        {
            UndoResult result = null;
            Locked(() => result = _job.Undo());
            return result;
        }

        static void Locked(Action work)
        {
            if (!Plugin.WorkLock.Wait(0))
                throw new InvalidOperationException("A cleanup is running right now. Try again when it has finished.");
            try
            {
                work();
            }
            finally
            {
                Plugin.WorkLock.Release();
            }
        }
    }
}
