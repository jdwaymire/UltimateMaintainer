// EmbyPlayState.cs
//
// IPlayStates on Emby's internal services: users, video libraries, and each user's play state (resume point,
// played, play count) of an item. The only file that touches Emby's user data, so an Emby update that breaks the
// cleanup should break here.
//
// NOTE: saving goes through IUserDataManager.SaveUserData with reason TogglePlayed, the same as the apps' "Mark
// played" button, so Emby tells the open apps and Continue Watching updates without a refresh.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using UltimateMaintainer.Core;

namespace UltimateMaintainer.Emby
{
    public class EmbyPlayState : IPlayStates
    {
        readonly ILibraryManager _library;
        readonly IUserManager _users;
        readonly IUserDataManager _userData;

        public EmbyPlayState(ILibraryManager library, IUserManager users, IUserDataManager userData)
        {
            _library = library;
            _users = users;
            _userData = userData;
        }

        // Collections and playlists hold the same movies again, and music/photos/books have no video resume points.
        static readonly HashSet<string> NotVideo = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "boxsets", "playlists", "music", "audiobooks", "books", "photos", "livetv" };

        public List<UserRef> GetUsers() =>
            _users.GetUserList(new UserQuery()).Select(u => new UserRef { Id = u.Id.ToString("N"), Name = u.Name }).ToList();

        public List<LibraryRef> GetLibraries() =>
            _library.GetVirtualFolders()
                .Where(f => !NotVideo.Contains(f.CollectionType ?? "") && !string.IsNullOrEmpty(f.ItemId))
                .Select(f => new LibraryRef { Id = f.ItemId, Name = f.Name, CollectionType = f.CollectionType ?? "" })
                .ToList();

        User User(string userId) => _users.GetUserById(userId) ?? throw new InvalidOperationException($"user {userId} not found");

        // IsResumable needs the user on the query: resume points are per user. Played items with a resume point
        // (rewatches) come back too, as they do in the apps' Continue Watching.
        public List<PartialItem> GetPartials(string userId)
        {
            var user = User(userId);
            var items = _library.GetItemList(new InternalItemsQuery
            {
                User = user,
                IsResumable = true,
                Recursive = true,
                MediaTypes = new[] { "Video" },
                IsVirtualItem = false,
            });
            // Never act on a half-loaded item (id 0 came back once with a short field list).
            if (items.Any(i => i.InternalId == 0 || string.IsNullOrEmpty(i.Name)))
                throw new InvalidOperationException("Emby returned items without id or name; nothing read");
            var result = new List<PartialItem>();
            foreach (var item in items)
            {
                var data = _userData.GetUserData(user, item);
                if (data == null || data.PlaybackPositionTicks <= 0)
                    continue;
                var library = _library.GetCollectionFolders(item).FirstOrDefault();
                var runTime = item.GetRunTimeTicksForPlayState();
                result.Add(new PartialItem
                {
                    ItemId = item.InternalId,
                    Title = Title(item),
                    LibraryId = library?.InternalId.ToString(),
                    LibraryName = library?.Name,
                    RunTimeTicks = runTime > 0 ? runTime : (long?)null,
                    LastPlayedUtc = data.LastPlayedDate?.UtcDateTime,
                    State = new PlayState { PositionTicks = data.PlaybackPositionTicks, Played = data.Played, PlayCount = data.PlayCount },
                });
            }
            return result;
        }

        // "Bluey S2E14 Sleepytime" for episodes, the plain name for everything else.
        static string Title(BaseItem item) =>
            item is Episode e ? $"{e.SeriesName} S{e.ParentIndexNumber}E{e.IndexNumber} {e.Name}" : item.Name;

        public PlayState Get(string userId, long itemId)
        {
            var user = _users.GetUserById(userId);
            var item = _library.GetItemById(itemId);
            if (user == null || item == null)
                return null;
            var data = _userData.GetUserData(user, item);
            return data == null ? null : new PlayState { PositionTicks = data.PlaybackPositionTicks, Played = data.Played, PlayCount = data.PlayCount };
        }

        // Only the three play-state fields change; last-played date, favourite, rating and track choices stay.
        public void Save(string userId, long itemId, PlayState state)
        {
            var user = User(userId);
            var item = _library.GetItemById(itemId) ?? throw new InvalidOperationException($"item {itemId} not found");
            var data = _userData.GetUserData(user, item);
            data.PlaybackPositionTicks = state.PositionTicks;
            data.Played = state.Played;
            data.PlayCount = state.PlayCount;
            _userData.SaveUserData(user, item, data, UserDataSaveReason.TogglePlayed, CancellationToken.None);
        }
    }
}
