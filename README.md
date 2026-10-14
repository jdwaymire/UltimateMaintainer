# Ultimate Maintainer for Emby

**Small, safe housekeeping jobs that keep an Emby server tidy, all on one settings page.**

[![Buy me a coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-support-FFDD00?logo=buymeacoffee&logoColor=black)](https://buymeacoffee.com/waymirenet)

Ultimate Maintainer is an Emby Server plugin with a handful of maintenance jobs that Emby does not do on its own:
clearing out Continue Watching entries nobody is ever going to finish, hiding "next episode" entries for shows that
were dropped years ago, and removing old devices that have not been seen in months.

## Why it exists

After a few years, Continue Watching on a family server fills up with half-watched movies, a show someone tried once,
and the next episode of every series anyone ever started. The entries that matter get lost between them. Emby has no
setting to let them expire, so they stay forever. The same goes for the Devices list: every browser, phone and TV
that ever signed in stays there.

Ultimate Maintainer cleans that up on a schedule, the same way every night, with one rule: **look first, then
change**. Every job is off until you switch it on, has a **Preview** that lists exactly what it would change, and the
Continue Watching jobs can **undo** their last run.

## The jobs

| Job | What it does | Undo |
|---|---|---|
| **Partial watch cleanup** | Items a user started and never finished stay in their Continue Watching for good. After a chosen number of days without playing, each is either **marked watched** or has its **resume point cleared** (you pick one for all). | Yes, the last run |
| **Next episode cleanup** | Continue Watching also offers the next episode of every series a user started, even shows dropped long ago. Once a series has not been played for a chosen number of days, that entry is hidden. | Yes, the last run |
| **Old devices cleanup** | Removes devices (Dashboard > Devices) that have not been seen for a chosen number of days. | No (use Preview) |

Every job works for **all users** or **only the users you tick**. The two Continue Watching jobs can also be limited
to **chosen libraries**.

### Partial watch cleanup

- **Days without playing:** how long an item may sit partly watched (default 14).
- **Action:** *Mark watched* (the item counts as played, the resume point goes) or *Clear the resume point only* (the
  item goes back to unwatched).
- An item the user had **already watched before** (a rewatch they stopped halfway) keeps its watched mark under
  either action; only the resume point goes.
- **Also clean up items with no play date** (off by default): some items have a resume point but no recorded play
  date, so the timeout cannot apply to them. They are listed in Preview and left alone unless you tick this.
- Items that are only a few percent in never get a resume point in Emby, and items past 90% are marked played by Emby
  itself, so the plugin needs no percentage rule of its own.

### Next episode cleanup

- **Days without playing:** counted from the last time the user played **any** episode of the series.
- Hiding works exactly like the apps' own **Hide from Continue Watching**: nothing is marked watched. When the user
  plays the series again, the entry comes back.
- A series with a partly watched episode is left to the partial watch cleanup (Continue Watching shows that episode
  instead).
- Only TV (and mixed) libraries are offered; movie libraries have no next episodes.

### Old devices cleanup

- **Days not seen:** at least 7.
- Removing a device signs that login out. If the device comes back, the user simply signs in again.
- A device with an open session (in use right now) is **never** removed, and every device is checked again just
  before it is removed.

## Requirements

- **Emby Server 4.10** or newer (built and used on 4.10.1, also compiles against 4.11 beta).

The plugin is a single file (`UltimateMaintainer.dll`) and brings no extra libraries.

## Installation

1. Download `UltimateMaintainer.dll` from the
   [Releases page](https://github.com/jdwaymire/UltimateMaintainer/releases).
2. Copy it into Emby's `plugins` folder:

   | System | Usual plugins folder |
   |---|---|
   | Linux (package install) | `/var/lib/emby/plugins/` |
   | Windows | `%AppData%\Emby-Server\programdata\plugins\` |
   | Docker | `<your config volume>/plugins/` |

   On Linux, make sure the file belongs to the user Emby runs as, e.g.
   `sudo chown emby:emby /var/lib/emby/plugins/UltimateMaintainer.dll`.
3. **Restart Emby Server.**
4. Open **Dashboard > Plugins**. *Ultimate Maintainer* is now in the list; click it for the settings page.

**Updating:** replace the DLL with the new one and restart Emby. Your settings and the undo records are kept.

## First run

1. Open the settings page. Every job is **off**.
2. Pick a job, set its days, users and (where offered) libraries.
3. Click **Preview**. It lists every item or device the job would change, per user, and changes nothing. On a server
   that has been running for years, the first run can easily touch hundreds of items, so take a look.
4. Happy with it? Either click **Run now** (it saves the page first and asks you to confirm with the count), or tick
   **Enabled** and save: the job then runs every night.
5. Changed your mind after a Continue Watching run? **Undo** puts back what the last run changed. An item someone has
   played since is left alone.

## Scheduled tasks

Each job has its own task under **Dashboard > Scheduled Tasks > Ultimate Maintainer**, daily by default:

| Task | Default time |
|---|---|
| Partial watch cleanup | 04:00 |
| Next episode cleanup | 04:05 |
| Old devices cleanup | 04:10 |

Change the times there like for any other Emby task. A disabled job's task starts and does nothing. Only one job runs
at a time: a scheduled task waits its turn, and a button on the settings page tells you when a job is busy.

A run that changes nothing (the usual quiet night) keeps the previous run's record, so **Undo** still undoes the last
run that actually changed something.

## Files and logs

All in Emby's `plugins` folder:

- `UltimateMaintainer.dll`, settings in `configurations/UltimateMaintainer.xml`
- `UltimateMaintainer/logs/<date>.log`: the plugin's own log (kept 30 days); the important lines also go to Emby's log
- `UltimateMaintainer/partial-watch/`, `next-up/`, `devices/`: each job's `last-run.json` (used by Undo and the
  "last run" line) and `runs/` with every run that changed something (kept 90 days)

## Building from source

You only need this if you want to change the plugin; the ready-made DLL is on the Releases page.

1. Install the [.NET SDK](https://dotnet.microsoft.com/download) (version 8 or newer).
2. In the repository folder, run:

   ```
   dotnet build -c Release src/UltimateMaintainer
   ```

3. The plugin is `src/UltimateMaintainer/bin/Release/netstandard2.0/UltimateMaintainer.dll`. Install it as described
   under [Installation](#installation).

The Emby SDK comes from NuGet (`MediaBrowser.Server.Core`) and is only used while compiling; Emby provides it at run
time, so the plugin stays a single DLL.

## Support

Ultimate Maintainer is free and always will be. I build tools like this mostly for myself and share them in case
they help someone else too. If it saved you some time, you can
[buy me a coffee](https://buymeacoffee.com/waymirenet). It keeps the tinkering going. Thank you!

[![Buy me a coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-support-FFDD00?logo=buymeacoffee&logoColor=black)](https://buymeacoffee.com/waymirenet)

## Disclaimer

Ultimate Maintainer is a free, hobby project, shared in the hope that it is useful to you. Please read this before
you install it:

- **Use it at your own risk.** It is provided as is, with no guarantees of any kind: not that it works on your server,
  not that it keeps working after an Emby update, and not that it is free of bugs.
- **It changes things on your server.** It changes play state (watched marks, resume points, hidden Continue
  Watching entries) and removes devices. It never touches your media files. Still, back up your Emby server before you
  start, and use **Preview** first.
- **Removed devices cannot be undone.** Their users just sign in again, but the plugin cannot bring an entry back.
- **No support is promised.** Questions and bug reports are welcome in the repository's Issues, and I will help where
  I can, but there is no guarantee of an answer or a fix.

The legal version of this is the [MIT License](LICENSE) the plugin is released under.

## Credits

Built for **[Emby](https://emby.media/)**; not affiliated with or endorsed by Emby LLC.
