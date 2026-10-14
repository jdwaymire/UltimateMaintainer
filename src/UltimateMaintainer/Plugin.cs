// Plugin.cs
//
// The plugin class Emby loads (name, GUID, settings page) and the settings it saves. Ultimate Maintainer is a home
// for several small maintenance jobs; each job has its own section of settings here, its own scheduled task and its
// own Core/ file. The first one is partial-watch cleanup (Core/PartialWatch.cs).
//
// NOTE: the GUID is also hard-coded in Web/configPage.js (pluginId). Change one and the settings page stops loading
// and saving the configuration. The page names in GetPages() must match the data-controller in configPage.html too.

using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using UltimateMaintainer.Core;

namespace UltimateMaintainer
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        public static Plugin Instance { get; private set; }

        // Held by every job that changes things (a scheduled run, "Run now", "Undo"), so two never run at the same
        // time. The scheduled task waits for it; the page's buttons are refused at once instead.
        public static readonly System.Threading.SemaphoreSlim WorkLock = new System.Threading.SemaphoreSlim(1, 1);

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer) : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public override string Name => "Ultimate Maintainer";

        public override string Description =>
            "Small maintenance jobs for your server: partly watched items, stale next episodes in Continue Watching, old devices.";

        public override Guid Id => new Guid("11b49b1a-0ead-49f0-9a27-f20f9733bcdc");

        // The picture on Emby's Plugins page: Images/thumb.png, an embedded resource.
        public Stream GetThumbImage() => GetType().Assembly.GetManifestResourceStream(GetType().Namespace + ".Images.thumb.png");

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        // The settings page is two embedded files. The HTML page loads the script as "__plugin/UltimateMaintainerJS",
        // which is the Name given here.
        public IEnumerable<PluginPageInfo> GetPages() => new[]
        {
            new PluginPageInfo
            {
                Name = "UltimateMaintainer",
                EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.html",
                IsMainConfigPage = true,
            },
            new PluginPageInfo
            {
                Name = "UltimateMaintainerJS",
                EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.js",
            },
        };
    }

    // The settings. Emby saves them as XML in plugins/configurations/UltimateMaintainer.xml. The property names are
    // also the keys configPage.js reads and writes, so renaming one loses the saved value.
    public class PluginConfiguration : BasePluginConfiguration
    {
        // ---- partial-watch cleanup (Core/PartialWatch.cs) ----

        // Off on a fresh install: the first run can change hundreds of items (over 300 on a real server, 2026-10-12), so the
        // admin looks at Preview first and then switches it on.
        public bool PartialWatchEnabled { get; set; }

        // An item last played this many days ago (or longer) is cleaned up.
        public int PartialWatchDays { get; set; } = 14;

        // watched | unwatched
        public string PartialWatchAction { get; set; } = PartialWatchSettings.Watched;

        // "all", or comma-separated Emby user ids.
        public string PartialWatchUsers { get; set; } = "all";

        // "all", or comma-separated library ids (the ItemId of Emby's library, e.g. "5").
        public string PartialWatchLibraries { get; set; } = "all";

        // Also clean up partly watched items that have no last-played date (they are never "old enough" otherwise).
        // Off by default: such an item may have been started yesterday by an app that does not report the date.
        public bool PartialWatchIncludeUndated { get; set; }

        public PartialWatchSettings GetPartialWatchSettings() =>
            PartialWatchSettings.From(PartialWatchDays, PartialWatchAction, PartialWatchUsers, PartialWatchLibraries, PartialWatchIncludeUndated);

        // ---- next-episode cleanup (Core/NextUp.cs) ----

        // Off on a fresh install, like the partial-watch cleanup: Preview first.
        public bool NextUpEnabled { get; set; }

        // A series not played for this many days loses its next-episode entry in Continue Watching.
        public int NextUpDays { get; set; } = 14;

        // "all", or comma-separated Emby user ids.
        public string NextUpUsers { get; set; } = "all";

        // "all", or comma-separated library ids.
        public string NextUpLibraries { get; set; } = "all";

        public NextUpSettings GetNextUpSettings() => NextUpSettings.From(NextUpDays, NextUpUsers, NextUpLibraries);

        // ---- old devices cleanup (Core/Devices.cs) ----

        // Off on a fresh install. There is no Undo for this job, so Preview first.
        public bool DevicesEnabled { get; set; }

        // A device not seen for this many days is removed (at least 7).
        public int DevicesDays { get; set; } = 90;

        // "all" (devices of any user, and devices with no known user), or comma-separated Emby user ids.
        public string DevicesUsers { get; set; } = "all";

        public DeviceSettings GetDeviceSettings() => DeviceSettings.From(DevicesDays, DevicesUsers);
    }
}
