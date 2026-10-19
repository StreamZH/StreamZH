# Settings reference

Every settings page, section by section. **(Advanced)** marks settings that are hidden until you turn on the
*Advanced* switch at the top of the page; a whole section marked (Advanced) is hidden as a whole. **(Owner)** marks
[owner-only settings](users.md#owner-only-settings). Defaults are given where they matter. Changes are saved per
section with the save bar that appears at the bottom.

- [Migration](#migration) · [Media Management](#media-management) · [Import lists](#import-lists) ·
  [Profiles](#profiles) · [Custom formats](#custom-formats) · [Quality](#quality) · [Indexers](#indexers) ·
  [Downloads](#downloads) · [Download clients](#download-clients) · [Usenet servers](#usenet-servers) ·
  [Connect](#connect) · [Requests](#requests) · [Discord](#discord) · [Metadata](#metadata) · [Users](#users) ·
  [General](#general) · [UI](#ui)

## Migration

Take over Radarr, Sonarr, Prowlarr and SABnzbd; see [MIGRATION.md](../MIGRATION.md).

| Setting | Meaning |
|---|---|
| Side by side: Grab nothing automatically | On: StreamZH searches and grabs only by hand (RSS sync, automatic and scheduled searches are off). **Switch over…** turns it off with a checklist for the old apps |
| Past runs | Every take-over and dry run with its report |

## Media Management

Where your movies and series live, how files are named and how finished downloads are imported.

### Root folders

The folders your movies and series live in. Each must be inside a [storage location](#storage); the owner can allow a
folder in one click. A folder StreamZH cannot write to shows **Read only** (on Windows often Controlled folder access:
allow `StreamZH.exe` in Windows Security → Ransomware protection).

### Movie naming

| Setting | Default | Meaning |
|---|---|---|
| Rename movies | on | Name imported files with the format below. Off: the download's file name is kept |
| Replace illegal characters (Advanced) | on | Replace characters Windows does not allow (`\ / ? * < > \| "`) instead of removing them, e.g. Face/Off → Face+Off |
| Colon replacement | Smart replace | What a colon in a title becomes: smart (dash or space dash), delete, dash, space dash, space dash space |
| Standard movie format | `{Movie Title} ({Release Year}) {Quality Full}` | File name. The token list below the field shows what is available |
| Movie folder format | `{Movie Title} ({Release Year})` | Used for movies you add from now on |

### Episode naming

| Setting | Default | Meaning |
|---|---|---|
| Rename episodes | on | Name imported files with the formats below |
| Standard episode format | `{Series Title} - S{season:00}E{episode:00} - {Episode Title} {Quality Full}` | |
| Daily episode format (Advanced) | `{Series Title} - {Air-Date} - {Episode Title} {Quality Full}` | For series of type Daily |
| Anime episode format (Advanced) | like the standard format | For series of type Anime; `{absolute:000}` gives the absolute episode number |
| Series folder format | `{Series TitleYear}` | Used for series you add from now on |
| Season folders (Advanced) | on | A folder per season. Off: all episodes straight into the series folder |
| Season folder format | `Season {season}` | |
| Specials folder format (Advanced) | `Specials` | Folder for season 0 |
| Multi-episode style | Prefixed range (`S01E01-E03`) | How a file with several episodes is named: extend, duplicate, repeat, scene, range, prefixed range |

### Importing

| Setting | Default | Meaning |
|---|---|---|
| Import mode (Advanced) | Move | Move is right for Usenet. *Copy or hard link* keeps the download, e.g. when another program still needs it. Torrents are always hard-linked or copied |
| Use hard links (Advanced) | on | Link instead of copy when download and library are on the same drive: no extra space, done at once |
| Skip free space check (Advanced) | off | Import even if the library drive looks full (for drives that report free space wrongly) |
| Minimum free space (Advanced) | 100 MB | Imports stop when less than this would stay free |
| Import extra files | off | Also import subtitles and other files next to the video, renamed to match it |
| Extra file extensions | `srt,ass,ssa,sub,idx` | A scene `.nfo` is saved as `.nfo-orig` |
| Episode title required (Advanced) | Always | Wait up to 48 hours after the air date when the episode title is not known yet and the format uses it (always, only for season packs, never) |

### File management

| Setting | Default | Meaning |
|---|---|---|
| Unmonitor deleted files | off | When a file disappears from disk, stop monitoring the item so it is not downloaded again |
| Propers and repacks (Advanced) | Prefer and upgrade | A PROPER or REPACK replaces the file; *Do not upgrade automatically*: preferred when grabbing, but no replacement; *Do not prefer*: ignored |
| Rescan folder after refresh (Advanced) | Always | Read the folder again after a metadata refresh (always, after a manual refresh, never) |
| Change file date (Advanced) | None | Set imported files to the release or air date |
| Recycling bin | empty | Replaced and deleted files go here instead of being deleted. Empty: deleted for good |
| Recycling bin cleanup (Advanced) | 7 days | Files older than this are deleted from the bin. 0: never |

### Media info

| Setting | Default | Meaning |
|---|---|---|
| Analyse video files | on | Read new and changed files with ffprobe at import and after library scans |
| Refresh media info (Advanced) | 7 days | Check the whole library for files that are new, changed or analysed by an older StreamZH. 0 = off |

### Folders (Advanced)

| Setting | Default | Meaning |
|---|---|---|
| Create empty folders | off | Create the movie or series folder when it is added, before there is a file |
| Delete empty folders | off | Remove folders that became empty during a disk scan, import or rename |

### Permissions (Advanced; Linux and macOS)

| Setting | Default | Meaning |
|---|---|---|
| Set permissions | off | Apply the mode and group below after an import |
| chmod folder | 755 | Octal mode for folders; files get the same without the execute bits |
| chown group | empty | Group name or id, e.g. `media`. StreamZH's account must be in that group |

### Completed and failed downloads

| Setting | Default | Meaning |
|---|---|---|
| Completed download handling | on | Import finished downloads automatically. Off: use *Import…* in Activity. Downloads from the Search page or *Add NZB* always need *Import…* |
| Remove completed downloads (Advanced) | off | Remove imported downloads from Activity → History |
| Redownload failed | on | Search again right away when a download fails; the failed release goes on the blocklist |
| Remove failed downloads (Advanced) | on | Remove failed downloads from Activity and delete their incomplete files |

## Import lists

Lists, options (clean library, update interval) and exclusions; see [Import lists](connect.md#import-lists).

## Profiles

Quality profiles, the default profile, and the TRaSH Guides section; see [Quality](quality.md).

| TRaSH Guides setting | Default | Meaning |
|---|---|---|
| Use the TRaSH Guides | on | Download the guides to offer their profiles and custom formats |
| Update automatically | on | Formats and profiles made from a template follow the guides after each sync |
| Keep my score changes | on | Scores you changed by hand stay when a profile is updated |
| Sync every | 24 hours | How often the guides are fetched |
| Repository, Branch (Advanced) | the TRaSH Guides on GitHub, `master` | A Git repository with the TRaSH Guides layout, e.g. your own fork |

## Custom formats

The formats with their conditions, import and export as JSON; see [Quality → Custom formats](quality.md#custom-formats).

## Quality

Minimum, preferred and maximum size per quality in MB per minute of runtime; see [Quality → Size limits](quality.md#size-limits).

## Indexers

Release rules for all indexers. The indexers themselves are under Indexers in the sidebar ([Indexers](indexers.md)).

### Options

| Setting | Default | Meaning |
|---|---|---|
| Retention | 0 (unlimited) | Your Usenet provider's retention in days. Older releases are rejected |
| Maximum size | 0 (unlimited) | Releases larger than this are rejected, whatever the quality |
| Minimum age (Advanced) | 0 (off) | New uploads need a moment to reach every Usenet server; younger releases wait for the next RSS sync |
| Allow hardcoded subs (Advanced) | off | Off: releases with burned-in subtitles (HC, KORSUB, Subbed …) are rejected |
| Whitelisted subtitle tags (Advanced) | | Tags that do not count as hardcoded subs, e.g. NLSUBS |
| Minimum availability (Advanced) | In cinemas | From when RSS and the missing search may grab a movie: announced, in cinemas, released (Released = cinema release + 90 days). The Search button always searches |
| Availability delay (Advanced) | 0 days | Added to that date; negative values start earlier |

### Usenet and torrents

| Setting | Default | Meaning |
|---|---|---|
| Preferred protocol | Usenet | On equal quality and score, the release of this protocol wins. *No preference*: the better indexer priority wins |
| Usenet delay, Torrent delay (Advanced) | 0 | A new release of that protocol waits this long before it may be grabbed |
| Skip delay for the best quality (Advanced) | on | A release with the highest allowed quality of the profile is grabbed at once |

### RSS & automation

| Setting | Default | Meaning |
|---|---|---|
| RSS sync interval | 60 minutes | How often the indexers' newest releases are read |
| Search for missing (Advanced) | 0 (off) | Search all missing movies and episodes regularly, on top of RSS (6 to 720 hours). Costs many API hits |
| Search when adding (Advanced) | off | The default of *Start search for missing* in the add dialog |
| Search alternative titles (Advanced) | on | When an indexer cannot search by id or finds nothing, also search the title in the profile language, scene names and your own titles (at most three per search) |

### Release profiles

Must contain, must not contain and preferred terms; see [Quality → Release profiles](quality.md#release-profiles).

## Downloads

How grabbed releases are downloaded, repaired and unpacked. With the blackhole folder only the first section is shown.
Defaults below are for the built-in engine.

### Download client

| Setting | Default | Meaning |
|---|---|---|
| Download client | StreamZH (built-in Usenet downloader) | Or *Blackhole folder*: grabbed NZBs are saved for SABnzbd, NZBGet or another downloader that watches the folder |
| Speed limit | 0 (unlimited) | KB/s; scheduler rules can change it at set times |
| NZB folder (blackhole only) | `<data>/downloads/nzb` | Passwords go into the file name as `title{{password}}.nzb` |

### Folders

| Setting | Default | Meaning |
|---|---|---|
| Watched folder | empty (off) | NZB files dropped here are downloaded and removed. A subfolder named after a category (`movies/`, `series/` …) gives that category |
| Scan interval (Advanced) | 5 seconds | How often the watched folder is checked |
| Incomplete folder (Advanced) | `<data>/downloads/incomplete` | Where downloads are assembled |
| Completed folder (Advanced) | `<data>/downloads/complete` | Finished downloads go into a subfolder per category; categories can point elsewhere |
| NZB backup folder (Advanced) | empty (off) | A copy of every added NZB is kept here |

### Categories

`movies`, `series` and `other`, plus your own. Each has a folder (empty: `<completed>/<name>`), a priority (Default,
Paused, Low, Normal, High, Force) and post-processing (None, +Repair, +Unpack, +Delete).

### Queue

| Setting | Default | Meaning |
|---|---|---|
| Duplicate detection | Off | A release with the same name or NZB in the queue or history: tag, pause or fail it. Failed downloads do not count |
| Pause downloading on start (Advanced) | off | The queue starts paused every time StreamZH starts |
| Check before download (Advanced) | off | Ask the servers (STAT) whether every article exists first; fail the download at once when par2 could not repair what is missing |
| Active downloads (Advanced) | 0 | Downloads that get articles at the same time. 1 = only the top of the queue (like SABnzbd); 0 = the next starts when the one before has nothing left to hand out |
| Propagation delay (Advanced) | 0 (off) | Wait until a release was posted this long ago, so all servers have it |
| Sort the queue (Advanced) | Off | Sort new downloads by age, size or name; the priority still comes first |
| Unwanted extensions (Advanced) | Off; `.exe, .scr, .bat, …` | Warn, pause or fail when the NZB or the unpacked files contain these. Executables are never moved to the completed folder anyway |
| Password-protected RAR (Advanced) | Off | Pause or fail a download whose RAR needs a password StreamZH does not know |
| Download all par2 files (Advanced) | off | Fetch every par2 recovery file right away. Off: only as many as a repair needs |
| Minimum free space (Advanced) | 1 GB | Downloads pause when less is free in the incomplete folder, and continue with 50 % more. 0 never pauses |

### Post-processing

| Setting | Default | Meaning |
|---|---|---|
| Ignore samples | on | Sample files are not moved to the completed folder |
| Deobfuscate final file names | off | A main file with a random name gets the name of the download, together with its subtitles |
| Abort hopeless downloads (Advanced) | on | Fail as soon as more is missing than par2 can repair, so another release can be tried |
| Only post-process verified downloads (Advanced) | on | Off: downloads par2 cannot verify or repair are unpacked and completed anyway, with a warning |
| Recursive unpacking (Advanced) | on | Archives inside archives are unpacked too (up to three levels) |
| Cleanup list (Advanced) | `.nfo, .sfv, .srr, .srs, .nzb, .md5, .url, .db` | Files with these extensions are deleted after unpacking |
| Temporary folder name (Advanced) | off | Post-processing writes into `_UNPACK_<name>` and renames the folder when done, so other programs do not pick up half-finished folders |
| Keep par2 and archive files (Advanced) | off | After a successful unpack the download folder stays in the incomplete folder |
| Post-processing workers (Advanced) | 1 | Downloads repaired and unpacked at the same time |

### Scheduling

Rules like "at HH:MM on these days do this": pause or resume downloads, set the speed limit (KB/s or a percentage of
the maximum line speed), pause or resume a server or a category. No rules: downloads run whenever something is queued.

### History

| Setting | Default | Meaning |
|---|---|---|
| Keep for | 0 (all) | Days finished downloads stay in the history |
| Keep at most (Advanced) | 0 (no limit) | The newest ones stay |
| Keep failed downloads (Advanced) | on | Failed downloads stay until you remove them, so they can be retried |

### Special (Advanced)

| Setting | Default | Meaning |
|---|---|---|
| Tries per article | 3 | How often an article is asked for again on one server after a timeout, before the next server |
| Maximum line speed | 0 (not set) | The speed of your line, for scheduler rules with a speed limit in percent |
| par2 and 7-Zip priority | Normal | Process priority of the tools |
| par2 parameters (Owner) | | Extra options for repairs: `-t` (threads), `-T`, `-m` (memory MB), `-N`, `-S`, `-q`, `-v` |
| 7-Zip parameters (Owner) | | Extra options for unpacking: `-mmt` (threads), `-bb0` to `-bb3`, `-scc`, `-mcp=`, `-ao`. Output folder and password are set by StreamZH |

### Tools (Owner)

Shows which par2, 7-Zip and ffprobe StreamZH found (installed copy, `PATH` or your path) and their versions, with
**Install** for missing ones.

| Setting | Default | Meaning |
|---|---|---|
| par2 path, 7-Zip path, ffprobe path (Advanced) | empty | A program with the tool's name (`par2`, `7z`, `ffprobe` …); empty: the installed copy or `PATH` |
| Install automatically (Advanced) | on | Missing tools are installed at startup into `<data>/tools` from their official releases (checked by SHA-256) |

## Download clients

Torrent clients (qBittorrent, Transmission, Deluge), the Usenet card, the built-in torrent engine and remote path
mappings (Advanced while empty); see [Downloads → Torrents](downloads.md#torrents).

## Usenet servers

Your Usenet provider accounts; see [Downloads → Usenet servers](downloads.md#usenet-servers).

## Connect

Media servers and notifications; see [Connect](connect.md).

## Requests

Approval, limits and where approved titles go; see [Requests → Settings](requests.md#settings--requests).

## Discord

The built-in Discord bot; see [Requests → Discord bot](requests.md#discord-bot).

## Metadata

### TMDB

| Setting | Default | Meaning |
|---|---|---|
| API token | | Your free TMDB *API Read Access Token* (or the shorter API key). Stored encrypted |
| Language | English (en-US) | For titles and descriptions. Changing it loads the whole library again |
| Certification country | US | Whose age ratings are used (NFO files, `{Movie Certification}`) |
| Original title in NFO (Advanced) | off | NFO files get the original title instead of the translated one |
| Local image cache (Advanced) | off | Posters and backgrounds load through StreamZH and are kept in the data folder, so browsers never contact TMDB |
| Refresh | | Loads titles, posters and episodes again for the whole library |

### Kodi (XBMC) / Emby

| Setting | Default | Meaning |
|---|---|---|
| Enable | off | Write NFO files and images next to your media |
| Movie metadata, Movie images | | An NFO named like the video file; `poster.jpg` and `fanart.jpg` |
| Use movie.nfo, Collection name (Advanced) | | Name the NFO `movie.nfo`; write the TMDB collection as a Kodi movie set |
| Series metadata, Episode metadata, Series/Season/Episode images | | `tvshow.nfo`, an NFO per episode, posters, season posters, episode thumbnails |
| Series metadata URL (Advanced) | | Adds the TMDB and TVDB links to `tvshow.nfo` |
| Whole library | | Writes the files for every movie and series now |

## Users

Accounts, roles, request limits and invite links; see [Users and roles](users.md).

## General

### Host (Owner, except the instance name)

| Setting | Default | Meaning |
|---|---|---|
| Instance name | StreamZH | Shown in the browser tab and on the sign-in page |
| Bind address (Advanced) | `*` | All network interfaces, or one IP address of this computer (`127.0.0.1` = only this computer) |
| Port | 9797 | |
| URL base | empty | For a reverse proxy that serves StreamZH below a path, e.g. `/streamzh`. The proxy must pass the path on unchanged |
| Application URL | empty | The address you open StreamZH with from outside, including the URL base. Used for links in notifications and trusted as StreamZH's own address |
| HTTPS (Advanced) | off | Serve HTTPS with your certificate. Behind a reverse proxy, leave it off |
| HTTPS port, Certificate, Certificate password (Advanced) | 9798 | A `.pfx` (PKCS #12) file with the private key |

Host changes apply after a restart. `--urls` or `ASPNETCORE_URLS` override bind address and port; the page then shows it.

### Security (Owner)

| Setting | Default | Meaning |
|---|---|---|
| Authentication | Forms | *Forms* (sign-in page), *Basic* (browser dialog; only over HTTPS, no sign-out), *External* (a reverse proxy signs people in) |
| Authentication required | Enabled | *Disabled for local addresses*: this computer and the home network need no sign-in (and act as owner) |
| Trusted local networks (Advanced) | empty | Only these addresses count as local, e.g. `192.168.1.0/24`. Empty: all private networks and this computer |
| Allowed host names (Advanced) | empty | Sign-in-free access is only given to IP addresses, `localhost`, this computer's name and the application URL; add every other name you open StreamZH with, e.g. `nas.lan` |
| Trusted proxies (Advanced) | empty | Your reverse proxies, e.g. `172.18.0.2` or `10.0.0.0/24`. Their `X-Forwarded-For`/`-Proto` headers count; with External only they may sign people in |
| Client IP header (Advanced) | Auto | Which header your proxy sets with the visitor's address: Auto, `X-Forwarded-For`, `X-Real-IP`, `CF-Connecting-IP` |
| User header (Advanced) | empty | External: the header your proxy sets with the signed-in user, e.g. `Remote-User` |
| Certificate validation (Advanced) | Enabled | HTTPS certificate checks of indexers, TMDB and other sites. *Disabled for local addresses* for your own servers with self-signed certificates; *Disabled* lets anyone on the way read indexer API keys |

Read [docs/SECURITY.md](../SECURITY.md#authentication-modes-and-their-risks) before you change these.

### API key (Owner)

The key for scripts and tools (`X-Api-Key` header). It acts as an Admin. **Create new key** replaces it at once.
See [API](api.md).

### Proxy (Owner; Advanced while off)

| Setting | Default | Meaning |
|---|---|---|
| Use proxy | off | Off: StreamZH uses the system proxy (`HTTP_PROXY`, `HTTPS_PROXY`, Windows settings), if there is one |
| Proxy type, Hostname, Port, Username, Password | HTTP, port 8080 | HTTP(S), SOCKS4 or SOCKS5; host name without `http://`; user name and password only if the proxy needs a login |
| Ignored addresses | | Go direct, separated by commas; `*` as wildcard |
| Bypass for local addresses | on | Your own servers (localhost, 192.168.x, names without a dot) go direct |

Usenet connections never go through the proxy.

### Logging

| Setting | Default | Meaning |
|---|---|---|
| Log level | Information | Debug and Trace help with troubleshooting, but write a lot |
| Log file size (Advanced) | 1 MB | A new file starts at this size |
| Old log files (Advanced) | 5 | How many older files are kept |
| Console log level (Advanced) | Information | What the terminal or `docker logs` shows |

### Backups (Owner)

| Setting | Default | Meaning |
|---|---|---|
| Folder (Advanced) | `<data>/Backups` | Another drive is safer. Scheduled backups go to `scheduled`, manual ones to `manual` |
| Interval | 7 days | Days between scheduled backups. 0 = no scheduled backups |
| Retention | 28 days | Scheduled backups older than this are deleted. Manual backups stay |

### Updates

Version, update mechanism for this installation, and **Check for updates** (asks GitHub for the latest release and
offers **Download <file>** with the file that fits this installation; StreamZH does not install updates by itself).

### Analytics

StreamZH sends no usage data, crash reports or analytics anywhere. It only talks to TMDB, your indexers, Usenet
servers and download clients, the services you connect, the TRaSH Guides and TheXEM, and to GitHub when you check
for updates.

### Storage

| Setting | Meaning |
|---|---|
| Storage locations (Owner) | The folders StreamZH may use for media and downloads. Root folders, download folders and everything else you pick must be inside one of them. A location cannot be removed while folders inside it are in use |
| Data folder | Database, settings, logs and keys. Set with `--data` or `STREAMZH_DATA` |
| Log folder | `<data>/logs` |

## UI

| Setting | Default | Meaning |
|---|---|---|
| Short date, Long date, Time format | the browser's locale | How dates and times are shown, for everyone on this StreamZH |
| Relative dates | on | "3 min ago" instead of the date and time for recent events |
| First day of week | Monday | Where the weeks of the calendar start |
| Theme | | Light or dark, remembered in this browser (also the sun and moon button in the top bar) |
| UI language | English | Titles and descriptions follow Settings → Metadata |
