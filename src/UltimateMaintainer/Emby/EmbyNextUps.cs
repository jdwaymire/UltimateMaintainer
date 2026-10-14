// EmbyNextUps.cs
//
// INextUps on Emby's internal services: each user's next-up entries (ITVSeriesManager.GetNextUp), when the user
// last played anything in each of those series, and hiding/unhiding an entry.
//
// NOTE: hiding goes through the episode's own UpdateHideFromResume, the same call the apps' "Hide from Continue
// Watching" ends in (REST POST /Users/{user}/Items/{id}/HideFromResume). The internal GetNextUp is the "legacy" next-up
// list; on 4.10.1 REST /Shows/NextUp?LegacyNextUp=true gave exactly the next-up entries Continue Watching showed,
// while the plain /Shows/NextUp came back empty (2026-10-12).

using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Querying;
using UltimateMaintainer.Core;

namespace UltimateMaintainer.Emby
{
    public class EmbyNextUps : INextUps
    {
        readonly ILibraryManager _library;
        readonly IUserManager _users;
        readonly IUserDataManager _userData;
        readonly ITVSeriesManager _tv;

        public EmbyNextUps(ILibraryManager library, IUserManager users, IUserDataManager userData, ITVSeriesManager tv)
        {
            _library = library;
            _users = users;
            _userData = userData;
            _tv = tv;
        }

        public List<UserRef> GetUsers() =>
            _users.GetUserList(new UserQuery()).Select(u => new UserRef { Id = u.Id.ToString("N"), Name = u.Name }).ToList();

        User User(string userId) => _users.GetUserById(userId) ?? throw new InvalidOperationException($"user {userId} not found");

        public List<NextUpItem> GetNextUps(string userId)
        {
            var user = User(userId);
            var episodes = _tv.GetNextUp(new NextUpQuery { UserId = user.InternalId, Limit = 1000 }, user, new DtoOptions(true)).Items
                .OfType<Episode>().ToList();
            if (episodes.Any(e => e.InternalId == 0 || string.IsNullOrEmpty(e.Name)))
                throw new InvalidOperationException("Emby returned episodes without id or name; nothing read");
            return episodes.Select(e =>
            {
                var library = _library.GetCollectionFolders(e).FirstOrDefault();
                var (lastPlayed, resumePoint) = SeriesActivity(user, e.SeriesId);
                return new NextUpItem
                {
                    EpisodeId = e.InternalId,
                    Series = e.SeriesName,
                    Episode = $"S{e.ParentIndexNumber}E{e.IndexNumber} {e.Name}",
                    LibraryId = library?.InternalId.ToString(),
                    LibraryName = library?.Name,
                    SeriesLastPlayedUtc = lastPlayed,
                    SeriesHasResumePoint = resumePoint,
                };
            }).ToList();
        }

        // The newest last-played date of any episode of the series for this user (started-but-not-finished counts
        // too), and whether any episode has a resume point. Only a handful of series per user, so reading every
        // episode's user data is fine.
        (DateTime?, bool) SeriesActivity(User user, long seriesId)
        {
            if (seriesId == 0)
                return (null, false);
            var resumePoint = false;
            DateTimeOffset? last = null;
            var episodes = _library.GetItemList(new InternalItemsQuery
            {
                AncestorIds = new[] { seriesId },
                IncludeItemTypes = new[] { "Episode" },
                Recursive = true,
                IsVirtualItem = false,
            });
            foreach (var e in episodes)
            {
                var data = _userData.GetUserData(user, e);
                if (data == null)
                    continue;
                if (data.PlaybackPositionTicks > 0)
                    resumePoint = true;
                if (data.LastPlayedDate != null && (last == null || data.LastPlayedDate > last))
                    last = data.LastPlayedDate;
            }
            return (last?.UtcDateTime, resumePoint);
        }

        public void SetHidden(string userId, long episodeId, bool hidden)
        {
            var user = User(userId);
            var item = _library.GetItemById(episodeId) ?? throw new InvalidOperationException($"episode {episodeId} not found");
            var data = _userData.GetUserData(user, item);
            item.UpdateHideFromResume(user, data, _library.GetCollectionFolders(item), hidden);
        }
    }
}
