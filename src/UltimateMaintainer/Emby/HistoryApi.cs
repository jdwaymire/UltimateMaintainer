// HistoryApi.cs
//
// REST route behind the settings page's History tab: the run history (Core/History.cs) with local times, newest first.
//
// NOTE: history.json only exists since 1.1.0; before the first run after an update the list is empty and the page
// says so.

using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;
using UltimateMaintainer.Core;

namespace UltimateMaintainer.Emby
{
    [Route("/UltimateMaintainer/History", "GET", Summary = "Every run and undo of every job, newest first")]
    [Authenticated(Roles = "Admin")]
    public class HistoryRequest : IReturn<List<HistoryView>>
    {
    }

    public class HistoryView
    {
        public string Date { get; set; }
        public string Job { get; set; }
        public string Kind { get; set; }
        public int Changed { get; set; }
        public string Action { get; set; }
        public int Recent { get; set; }
        public int Skipped { get; set; }
        public int Errors { get; set; }
    }

    public class HistoryApi : IService
    {
        readonly EmbyJson _json;

        public HistoryApi(IJsonSerializer jsonSerializer)
        {
            _json = new EmbyJson(jsonSerializer);
        }

        public object Get(HistoryRequest request) =>
            (JsonFiles.Load<List<HistoryEntry>>(_json, JsonFiles.HistoryPath) ?? new List<HistoryEntry>())
                .Where(e => e != null)
                .Select(e => new HistoryView
                {
                    Date = e.DateUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    Job = e.Job,
                    Kind = e.Kind,
                    Changed = e.Changed,
                    Action = e.Action,
                    Recent = e.Recent,
                    Skipped = e.Skipped,
                    Errors = e.Errors,
                }).ToList();
    }
}
