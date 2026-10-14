// EmbyDevices.cs
//
// IDevices on Emby's internal services: the device list (the same as Dashboard > Devices), which devices have an
// open session right now, and removing devices (IDeviceManager.DeleteDevices, as the dashboard's delete button).
//
// NOTE: DeviceInfo.LastUserId is turned into the user's id in the "N" spelling the settings page saves, by looking the
// user up, so the user filter matches whatever spelling Emby hands out here.

using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Devices;
using UltimateMaintainer.Core;

namespace UltimateMaintainer.Emby
{
    public class EmbyDevices : IDevices
    {
        readonly IDeviceManager _devices;
        readonly ISessionManager _sessions;
        readonly IUserManager _users;

        public EmbyDevices(IDeviceManager devices, ISessionManager sessions, IUserManager users)
        {
            _devices = devices;
            _sessions = sessions;
            _users = users;
        }

        public List<DeviceEntry> GetDevices()
        {
            var inUse = new HashSet<long>(_sessions.Sessions.Select(s => s.InternalDeviceId));
            var inUseReported = new HashSet<string>(_sessions.Sessions.Where(s => s.DeviceId != null).Select(s => s.DeviceId));
            var users = new Dictionary<string, (string Id, string Name)>();
            return _devices.GetDevices(new DeviceQuery()).Items.Select(d =>
            {
                var user = User(users, d.LastUserId);
                return new DeviceEntry
                {
                    Id = d.InternalId,
                    Name = d.Name,
                    App = d.AppName,
                    UserId = user.Id,
                    UserName = user.Name ?? d.LastUserName,
                    LastActivityUtc = d.DateLastActivity.Year < 2000 ? (DateTime?)null : d.DateLastActivity.UtcDateTime,
                    InUse = inUse.Contains(d.InternalId) || (d.ReportedDeviceId != null && inUseReported.Contains(d.ReportedDeviceId)),
                };
            }).Where(d => d.Id != 0).ToList();
        }

        // Cached per call: a few hundred devices share about ten users.
        (string Id, string Name) User(Dictionary<string, (string, string)> cache, string lastUserId)
        {
            if (string.IsNullOrEmpty(lastUserId))
                return (null, null);
            if (!cache.TryGetValue(lastUserId, out var u))
            {
                // Either a user guid or Emby's internal number, depending on the Emby version.
                MediaBrowser.Controller.Entities.User user = null;
                try
                {
                    user = lastUserId.Length < 20 && long.TryParse(lastUserId, out var n) ? _users.GetUserById(n) : _users.GetUserById(lastUserId);
                }
                catch (Exception)
                {
                    // unknown id: shown as "no user"
                }
                cache[lastUserId] = u = user == null ? (null, null) : (user.Id.ToString("N"), user.Name);
            }
            return u;
        }

        public void Delete(long[] ids) => _devices.DeleteDevices(ids);
    }
}
