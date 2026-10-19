# System

The **System** pages (sidebar) show how StreamZH itself is doing.

## Status

- **Runs as**: Windows service, systemd service, Docker container or program (console); **Listening on**: the
  addresses StreamZH answers on; **Data folder**, database size, version, .NET runtime, operating system, uptime.
- **Health**: the same warnings as the status window (no indexer, no Usenet server, a root folder not found, low disk
  space, a failing indexer, an expiring VIP or Usenet account, …), each with a link to where it is fixed.
- **Disk space** of the data folder, the download folders, the backup folder and the root folders.
- **Restart** and **Shut down**. A restart runs StreamZH's start again in the same process (it works the same in a
  console, as a service and in Docker); downloads pause and continue. Shut down stops StreamZH until it is started
  again where it runs (the service, the terminal, Docker).

## Tasks

**Scheduled**: the tasks StreamZH runs by itself, with their interval, last and next run, and **Run now**:

| Task | Default interval | Does |
|---|---|---|
| RSS sync | 60 minutes | Reads the newest releases of all indexers and grabs what is needed |
| Search for missing | off | Searches all missing items ([Settings → Indexers](settings.md#rss--automation)) |
| Process pending releases | while releases wait | Grabs releases whose protocol delay is over |
| Check waiting imports | 5 minutes | Tries held-back imports again (episode title not known yet) |
| Import list sync | 24 hours | Reads the import lists |
| Request availability | 15 minutes | Marks requests available and tells requesters |
| Refresh media info | 7 days | Reads new and changed files with ffprobe |
| Scene mappings | 12 hours | Loads TheXEM scene numbering and scene names |
| TRaSH Guides sync | 24 hours | Updates profiles and custom formats from the TRaSH Guides |
| Backup | 7 days | Scheduled backup |
| Clean up recycle bin | when a recycle bin is set | Deletes old files from the recycle bin |
| Download history cleanup | when a limit is set | Removes old downloads from the history |

**Queue**: refreshes, searches, imports and other work wait here and run one job at a time. The status window shows
the running one too.

## Backup

Backups of the database (settings, library, history, users) and the sign-in keys, as zip files; media files are not
part of them. Owner only: a backup holds every password and key.

- **Backup now**, and scheduled backups (Settings → General → Backups: every 7 days, kept 28 days).
- Download or delete a backup.
- **Restore**: a backup from the list, or **Restore from a file** (a zip from this or another StreamZH; backups from a
  newer StreamZH are refused). It is checked, staged, and applied at the next start; restart to restore. The replaced
  database is kept in the data folder under `restore`.

See [INSTALL.md → Backups and restore](../INSTALL.md#backups-and-restore).

## Logs

**Entries**: the current log file, newest first, filtered by level (debug and above, info and above, warnings and
errors) and text. **Log files**: download or delete them; attach them to a bug report (check them for anything private
first). How much is written: Settings → General → Logging. The files are in `<data>/logs`.

## Updates

Settings → General → **Updates** shows the version, how this installation is updated, and **Check for updates**,
which asks GitHub for the latest release. When a newer version is available it shows **Download <file>** with the
file that fits this installation (setup, `.deb`, `.rpm`, `.zip` or `.tar.gz` for this platform and processor; for
Docker, pull the image) and a link to the release notes. StreamZH does not install updates itself; see
[INSTALL.md → Updating](../INSTALL.md#updating).

## The status window

The **status window** sits at the bottom right for owners and admins, whenever something runs or needs attention.
Collapsed it shows one line (the current work with percent and speed or time); expanded it lists what is happening
right now (downloads with percent and time left, imports, searches, organize runs, tool installations, background
jobs) and the notices: warnings and errors with a link to the fix, and information such as "Restart required" or the
side-by-side mode. Some notices can be dismissed. The top bar shows the downloads as well, and on Windows the tray
icon shows the same state.
