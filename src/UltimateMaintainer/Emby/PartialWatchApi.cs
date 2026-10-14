// PartialWatchApi.cs
//
// REST routes behind the settings page's "Partial watch cleanup" section: the video library list, Preview (with the
// page's unsaved values, changes nothing), Run now and Undo (with the saved settings), and the last-run line.
//
// NOTE: all routes are admin only; Preview shows every user's viewing. Run and Undo are refused straight away while
// another job holds WorkLock, instead of waiting, so the page can say so.

using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;
using UltimateMaintainer.Core;

namespace UltimateMaintainer.Emby
{
    [Route("/UltimateMaintainer/Libraries", "GET", Summary = "Video libraries the cleanup can be limited to")]
    [Authenticated(Roles = "Admin")]
    public class LibrariesRequest : IReturn<List<LibraryRef>>
    {
    }

    [Route("/UltimateMaintainer/PartialWatch/Preview", "POST", Summary = "What a cleanup with these settings would change (changes nothing)")]
    [Authenticated(Roles = "Admin")]
    public class PartialWatchPreviewRequest : IReturn<PartialWatchPreview>
    {
        public int Days { get; set; }
        public string Action { get; set; }
        public string Users { get; set; }
        public string Libraries { get; set; }
        public bool IncludeUndated { get; set; }
    }

    public class PartialWatchPreview
    {
        public List<PreviewRow> Rows { get; set; } = new List<PreviewRow>();
        public List<SkippedItem> Skipped { get; set; } = new List<SkippedItem>();
        public int Recent { get; set; }
        public int UsersChecked { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    public class PreviewRow
    {
        public long ItemId { get; set; }                // for the poster on the settings page
        public string User { get; set; }
        public string Title { get; set; }
        public string Library { get; set; }
        public int? Percent { get; set; }
        public int DaysIdle { get; set; }
        public bool WasPlayed { get; set; }
        public string Result { get; set; }
    }

    [Route("/UltimateMaintainer/PartialWatch/Run", "POST", Summary = "Run the cleanup now with the saved settings")]
    [Authenticated(Roles = "Admin")]
    public class PartialWatchRunRequest : IReturn<LastRunView>
    {
    }

    [Route("/UltimateMaintainer/PartialWatch/LastRun", "GET", Summary = "The last cleanup that changed something")]
    [Authenticated(Roles = "Admin")]
    public class PartialWatchLastRunRequest : IReturn<LastRunView>
    {
    }

    public class LastRunView
    {
        public bool Exists { get; set; }
        public string Date { get; set; }
        public int Changed { get; set; }
        public string Action { get; set; }
        public bool Undone { get; set; }

        // The latest run, quiet or not (see LatestRun); LatestDate stays null until a run since 1.0.1.
        public string LatestDate { get; set; }
        public int LatestChanged { get; set; }
        public string LatestAction { get; set; }
        public int LatestRecent { get; set; }
        public int LatestSkipped { get; set; }
        public int LatestErrors { get; set; }

        public LastRunView With(LatestRun l)
        {
            if (l != null)
            {
                LatestDate = l.DateUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                LatestChanged = l.Changed;
                LatestAction = l.Action;
                LatestRecent = l.Recent;
                LatestSkipped = l.Skipped;
                LatestErrors = l.Errors;
            }
            return this;
        }
    }

    [Route("/UltimateMaintainer/PartialWatch/Undo", "POST", Summary = "Put back what the last cleanup changed")]
    [Authenticated(Roles = "Admin")]
    public class PartialWatchUndoRequest : IReturn<UndoResult>
    {
    }

    public class PartialWatchApi : IService
    {
        readonly PartialWatchJob _job;

        public PartialWatchApi(ILibraryManager library, IUserManager users, IUserDataManager userData, ILogManager logManager,
            IJsonSerializer jsonSerializer)
        {
            _job = new PartialWatchJob(library, users, userData, logManager, jsonSerializer);
        }

        public object Get(LibrariesRequest request) => _job.Emby.GetLibraries();

        public object Post(PartialWatchPreviewRequest request)
        {
            var plan = _job.Preview(PartialWatchSettings.From(request.Days, request.Action, request.Users, request.Libraries, request.IncludeUndated));
            return new PartialWatchPreview
            {
                Rows = plan.Changes.Select(c => new PreviewRow
                {
                    ItemId = c.ItemId,
                    User = c.UserName,
                    Title = c.Title,
                    Library = c.Library,
                    Percent = c.Percent,
                    DaysIdle = c.DaysIdle,
                    WasPlayed = c.Old.Played,
                    Result = c.Result,
                }).ToList(),
                Skipped = plan.Skipped,
                Recent = plan.Recent,
                UsersChecked = plan.UsersChecked,
                Errors = plan.Errors,
            };
        }

        public object Post(PartialWatchRunRequest request)
        {
            Locked(() => _job.Run(manual: true));
            return Get(new PartialWatchLastRunRequest());
        }

        public object Get(PartialWatchLastRunRequest request)
        {
            var r = _job.LastRun();
            return (r == null
                ? new LastRunView()
                : new LastRunView
                {
                    Exists = true,
                    Date = r.DateUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    Changed = r.Changes.Count,
                    Action = r.Action,
                    Undone = r.Undone,
                }).With(_job.Latest());
        }

        public object Post(PartialWatchUndoRequest request)
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
