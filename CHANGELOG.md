# Changelog

All notable changes for users of StreamZH. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [Semantic Versioning](https://semver.org/). Versions before 1.0 were private test versions.

## [Unreleased]

### Changed

- **Import from Prowlarr uses the provider's own address.** Every indexer with an API of its own (Newznab, Torznab)
  gets the indexer's own URL, from Prowlarr or from the catalog when Prowlarr does not show it, never Prowlarr's
  address (`http://prowlarr:9696/1/api`). Prowlarr hides the API keys: type them in the import or the migration
  wizard; an indexer without a key is added switched off until its key is entered under Edit. Only site scrapers
  without an API of their own (e.g. Cardigann trackers, NZBIndex) still go through Prowlarr. API: the import mode
  `needsKey` is new.
- **Test in the indexer details.** The details view has a **Test** button next to Edit; it reads the search
  capabilities and categories right there. A successful test (here, in the list or in Edit) also ends the waiting
  time after earlier failures.
- **Indexers that still go through Prowlarr** (imported with an older version) are marked in their details and in
  Edit, with **Use the indexer's own address**. Edit shows the URL without the *Advanced* switch whenever it differs
  from the catalog address, and *Reset* restores the API path too.

## [1.0.0-rc.4] - unreleased

### Changed

- **Tidier navigation, like CloudZH.** The sidebar is down to twelve entries: Overview · Library (Movies, Series,
  Calendar, Wanted) · Requests (Discover, Requests) · Downloads (Activity, Indexers) · Administration (Tools, Settings,
  System). Pages that belong together are tabs in the main window: **Settings** groups its pages in Media, Downloads,
  Connections and General (the pages of a group as a second row), **System** has Status, Tasks, Backup and Logs,
  **Tools** has File renamer and Release test. All addresses stay the same, Ctrl K finds every page, and the tabs work
  with the keyboard (arrow keys) and scroll sideways on a phone.
- **Migration is for advanced use:** its Settings tab shows with the *Advanced* switch on, and by itself while the
  side-by-side mode is on or a migration runs. **Start migration** on the Get started page (and on the Overview while
  the library is empty) still leads there.
- **Get started → Release language** offers your own language instead of always German: the language of your browser
  (or the TMDB language), e.g. *Turkish* with "Turkish 1080p" as default profile, created like "Deutsch 1080p".
  German still uses "Deutsch 1080p", and installations that chose German look the same. English browsers see *Any
  language* and *Original language*. API: `PUT /api/v1/setup/guide/language` also takes
  `{ "language": "native", "code": "tr" }`.
- The Docker image carries a description and links to streamzh.com.

## [1.0.0-rc.3] - 2026-10-21

### Security

- **Linux service:** the systemd unit is hardened further (no devices, kernel settings, namespaces, capabilities or
  SUID; only Unix and IP sockets). Existing installations get it with the package update; with `install-linux.sh`,
  run the script again.
- **Docker:** StreamZH runs with "no new privileges" after dropping root, and the image no longer contains debug
  symbols or the unused port setting 8080 from the base image.

### Changed

- Release notes keep their paragraphs on one line and link to the installation guide on streamzh.com.

## [1.0.0-rc.2] - 2026-10-20

### Changed

- **Simpler, branded Windows installer.** New look with the StreamZH logo (follows Windows' light or dark mode, sharp
  at every display scaling) and fewer pages: Welcome, "How should StreamZH run?" (Windows service or only while you are
  signed in, plus the firewall, Controlled folder access and desktop shortcut options on the same page), for the
  service "Where are your movies and series?" (nothing to set up for another drive or a NAS, one click for your Videos
  and Downloads folders, or folders of your choice), Ready, Finish. No license and no folder page. The Finish page
  has **Start StreamZH** and **Open StreamZH in my browser**. Updates show only Welcome, Ready and Finish, keep your
  choices, close StreamZH and its tray icon by themselves and start it again.
- **Start menu:** "StreamZH" opens the web interface, "StreamZH tray icon" starts the tray icon; both directly in the
  Start menu instead of a folder. Optional desktop shortcut.
- **Simpler uninstaller:** one question, "Remove StreamZH from this computer?", with the unticked box "Also delete my
  data" and a clear note what stays or goes, then progress and a short final message. Silent switches as before
  (`/VERYSILENT`, `/DELETEDATA=1`).
- **Tray-only mode:** StreamZH runs without a console window.

### Fixed

- The installer's "Open StreamZH in the browser" did nothing: its PowerShell command had a syntax error.
- Duplicate detection: grabbing a release again right after its download failed could be refused as a duplicate,
  because the failure was not yet saved. Failed downloads never count as duplicates.

## [1.0.0-rc.1] - unreleased

The first public release candidate: download, install, configure, use.

### Added

- **Windows installer** (`StreamZH-<version>-win-x64-setup.exe`): installs to `C:\Program Files\StreamZH` (only
  administrators may change it) and keeps the data in `C:\ProgramData\StreamZH`. The wizard asks how StreamZH runs
  (Windows service, the default, or tray icon only), offers a firewall rule for TCP 9797 from private networks and an
  exception in Controlled folder access, and up to three folders the service account may change. Running a newer
  setup updates in place with the same choices; an older manual installation is moved over. The uninstaller keeps
  your data unless you tick "Also delete data". Silent installs with `/VERYSILENT /TASKS=... /FOLDERS=...`.
- **Portable Windows zip** (`StreamZH-<version>-win-x64.zip`) with the service and tray scripts.
- **Tray icon only** mode on Windows (`tools\Install-Tray.ps1`, or the installer's choice): StreamZH runs as your
  user while you are signed in, with your mapped drives; the tray icon starts it with Windows.
- **Linux packages**: `.deb` and `.rpm` for x64 and arm64. They install to `/opt/streamzh`, create the user
  `streamzh` and `/var/lib/streamzh`, enable and start `streamzh.service`, restart it on upgrade and keep your data on
  removal (`apt purge` deletes it). The `.tar.gz` with `install-linux.sh` stays for other distributions.
- **Docker image on GHCR** (`ghcr.io/streamzh/streamzh`) for amd64 and arm64, tagged with the version, the minor
  version and `latest` (release candidates only by their version), with the torrent port 51414 exposed.
- **macOS** zips for Apple silicon and Intel (not notarized: remove the quarantine flag once).
- **Releases** with checksums (`SHA256SUMS`) and these notes, built from a version tag.
- **Get started** (`/get-started`): the page a new owner lands on after creating the account. Six steps (storage
  locations, root folders, TMDB key, release language, indexers, a download client), most of them done right on the
  page, each ticked off from the real configuration, so the migration wizard fills them too. Shortcuts to the
  migration, invites, the Discord bot, media servers and library import. The Overview shows "Setup: n of 6 done"
  with the next step until everything is set up or you hide it.
- **Release language** step: any language, the original language or German; it sets the default quality profile.
- **Documentation**: a user guide (`docs/guide`), an installation guide for every platform with reverse proxy
  examples (`docs/INSTALL.md`), contributing guide, security policy, code of conduct, issue and pull request templates.

### Changed

- **New installations default to the quality profile "HD-1080p" in any language** instead of "Deutsch 1080p".
  "Deutsch 1080p" is still there and is chosen with the German release language. Existing installations keep their
  profiles and default.
- The update check looks at the project's GitHub releases and offers **Download <file>** with the file that fits this
  installation (setup, `.deb`, `.rpm`, `.zip` or `.tar.gz`, by platform and processor), and how to update it.
- A port that is already in use stops StreamZH with one clear message (which port, how to choose another one)
  instead of a stack trace; the Windows service writes it to the event log and the tray icon shows it.
- Discover without a TMDB key shows one notice with a link to add the key instead of an error in every row.
- The Activity totals and the top bar (speed, count, time left) include torrent downloads; a paused Usenet queue no
  longer hides the torrent speed. No "No Usenet server" warning when torrents are set up.
- Malformed request bodies (for example an unknown value in a list of choices) are answered with a field error
  instead of a server error.
- Requesters' live updates follow role changes at once.
- Requests no longer wait for each other while TMDB answers slowly, and two requests for the same title add it once.
- The "Program folder not protected" notice names the setup and `Install-Tray.ps1` as fixes.
- Install and service scripts speak English throughout; `install-linux.sh` leaves a `.deb` or `.rpm` installation
  to the package manager.
- Settings → UI no longer promises a German interface (the interface stays English).

### Security

- Release files come with build provenance attestations; the release workflow refuses to run in a repository other
  than the one the app checks for updates, and all GitHub Actions are pinned to commit hashes.
- The update check accepts only release links of the StreamZH repository itself.
- The Windows setup installs only inside Program Files (unless `/ALLOWCUSTOMDIR`, which protects the folder before
  copying), refuses "tray only" when it runs as SYSTEM, and refuses a data folder that is a link or was prepared by
  another user; folder names reach the service script without being turned into PowerShell code.
- StreamZH warns "Data folder not private" when the data folder's owner is not an administrator or StreamZH's own
  account, or when keys, Backups, restore or tools in it is a link. The install scripts create the data folder with its
  final owner and access list in one step and reset items that carry access for other accounts.

## [0.12.0] - 2026-10-19

Security and privacy review.

### Added

- **Setup code**: a new installation can only be claimed on the server itself (by `localhost`, an IP address or the
  computer's name) or with a one-time code from the console, the log, the `setup-code` file or the tray icon.
- **Storage locations** (Settings → General → Storage, owner only): every folder StreamZH may use must be inside one.
  Upgrades add every folder in use automatically; the root-folder dialog and the migration wizard allow a folder in
  one click.
- **Owner-only settings**: API key, security, host, outgoing proxy, backups, tool paths and storage locations.
- Security settings: trusted local networks, trusted proxies, client IP header, allowed host names, user header for
  External authentication; "Remember me" on the sign-in page; "Sign out everywhere".
- The iCal feed has its own read-only token.
- Status notices: "Program folder not protected", "secret unreadable", "No sign-in for local addresses in a container".

### Changed

- **Stored secrets are encrypted** (indexer keys, Usenet and download client passwords, notification secrets including
  webhook headers and tokens in URLs, Discord bot token, TMDB token, proxy password, the API key).
- **The API key acts as an Admin**, no longer as the owner.
- A stored secret is only reused for an unchanged destination; a changed address needs the secret typed again.
- Cross-site requests, DNS rebinding and clickjacking are refused; strict Content Security Policy and security headers.
- Server-side sessions: signing out, password and role changes end sessions and live connections at once.
- Sign-in attempts are rate limited per client and failures counted per account and network.
- `?apikey=` in addresses only works for the live connection; download links keep indexer keys out of the history.
- Other local names (`nas`, `*.local`, `*.lan`) no longer get sign-in-free access unless listed under Allowed host
  names.
- The Windows service runs as the virtual account `NT SERVICE\StreamZH` instead of LocalSystem; the data folder is
  private on every platform (and the sign-in keys DPAPI-protected on Windows); the install scripts protect the program
  folder.
- No folder for downloads, imports, renames or backups may be inside the data folder or the program folder; tools only
  run from verified places, and installed tools are checked by their SHA-256 before every run.
- Outgoing requests: links from indexers may not lead to this computer, cloud metadata addresses or (since this
  version) other LAN hosts than the configured services; size limits for answers.
- Archives: links created by unpacking are deleted; an old 7-Zip on `PATH` is replaced by the pinned one.

## [0.11.0] - 2026-10-19

### Added

- **Torrents**: Torznab indexers (seeders, peers, magnet links, freeleech and other flags, minimum seeders, seed goals).
- **qBittorrent, Transmission and Deluge** as download clients, and a **built-in torrent engine**.
- Torrent tracking in Activity; import by hard link or copy so seeding never breaks; seed goals and removal after
  seeding; remote path mappings.
- Usenet/torrent **protocol preference** with delays and pending releases.
- The migration takes over torrent indexers and torrent clients.

## [0.10.0] - 2026-10-19

### Added

- **User management**: roles Owner, Admin, Friend and Requester; invite links; per-user auto-approve and request limits.
- **Discover**: TMDB trending, popular, upcoming and top rated, and search.
- **Requests** with approval, auto-approve, limits and availability tracking; request notifications in Connect.
- A built-in **Discord bot** (`/movie`, `/tv`, `/requests`, `/link`, direct messages) with an import of Requestrr's
  settings.

## [0.9.0] - 2026-10-19

### Added

- **Migration wizard** (Settings → Migration): takes over Radarr, Sonarr, Prowlarr and SABnzbd through their APIs,
  read-only: root folders, quality sizes, custom formats and scores, quality profiles, release profiles, naming,
  media management, notifications, list exclusions, indexers, Usenet servers, categories, speed and schedule, movies
  and series with their existing files (linked, never moved), history and blocklist. Path mappings, preview, dry run
  and report.
- **Side-by-side mode**: StreamZH grabs nothing automatically until you switch over.
- `docs/MIGRATION.md` and a Docker Compose example.

## [0.8.1] - 2026-10-19

### Added

- **Alternative titles**: TMDB titles and translations and your own titles, used for matching and searching.
- **Series types** Standard, Daily and Anime, with absolute numbering and TheXEM scene numbering.
- **Media info** with ffprobe (installed like par2 and 7-Zip on Windows), MediaInfo naming tokens and audio languages.

### Fixed

- Small fixes in the import and the file renamer.

## [0.8.0] - 2026-10-18

### Added

- **Custom formats** like Radarr and Sonarr: all condition types, scores in profiles, minimum score, upgrade until
  score, quality groups, the `{Custom Formats}` token, JSON import and export.
- **TRaSH Guides sync** built in: templates (including German profiles), format groups, quality sizes, a daily sync
  that keeps your own scores; `recyclarr.yml` import.
- **Windows tray icon** with status, downloads and a menu.

## [0.7.1] - 2026-10-18

### Changed

- The CloudZH look: light and dark palette, fonts, icons, a sidebar with groups, "search or jump to" (Ctrl K), downloads
  in the top bar, a splash screen and page transitions; every page restyled; a simple mode for the file renamer.

## [0.7.0] - 2026-10-18

### Added

- **Wanted** (missing, cutoff unmet) and a **calendar** with an iCal feed.
- Library **mass editor** and **Organize** (rename folders and files) with undo; move to another root folder.
- **File renamer** with rules, filters, regular expressions and episode titles from TMDB, with undo.
- **Import lists**: TMDB, Trakt, Plex watchlist, RSS and other StreamZH instances.
- Activity paging and filters.
- Run unattended: Windows service, systemd service with an install script, Docker `PUID`/`PGID` and a health check.

## [0.6.1] - 2026-10-18

### Fixed

- A finished download waiting for its import no longer causes a second download of the same movie.

## [0.6.0] - 2026-10-18

### Added

- **Import and rename** finished downloads into the library; blocklist and retry after failed downloads.
- **Connect**: Plex, Jellyfin, Emby and Kodi library updates and notifications; NFO files and artwork.
- Settings areas with the depth of Radarr, Sonarr and SABnzbd: Media Management, Indexers (release rules, release
  profiles, RSS sync), Downloads (categories, switches, scheduler, watched folder), Connect, Metadata, General (host,
  security, proxy, logging, backups, updates), UI; System pages (status, tasks, backups, logs).

## [0.5.2] - 2026-10-17

### Fixed

- Search is only skipped while the item is in the download queue, and the button says why a search was skipped.

## [0.5.1] - 2026-10-17

### Added

- The **Advanced** switch for expert settings; Usenet expert settings.
- par2 and 7-Zip are installed by StreamZH itself when they are missing.

## [0.5.0] - 2026-10-17

### Added

- The built-in **Usenet download engine**: NNTP with backup servers, yEnc, par2 verify and repair, unpacking.
- **Activity** page and the status window.

## [0.4.1] - 2026-10-17

### Added

- **Prowlarr built in**: indexer catalog, query and grab limits, VIP expiry, search across indexers, stats, history,
  import from Prowlarr.

## [0.4.0] - 2026-10-17

### Added

- Newznab indexers, manual and automatic search, grab to an NZB folder, grab history.

## [0.3.0] - 2026-10-17

### Added

- Release name, quality and language parser (ported from Radarr and Sonarr), quality profiles and the decision engine.
- Release test page.

## [0.2.1] - 2026-10-17

### Fixed

- Regional TMDB languages (de-CH, de-AT, fr-CH, it-CH) use the main language's titles; refresh all metadata.
- Import: better matching of folder names; manual TMDB search per folder.
- Unpacked Blu-ray and DVD folders and ISO images count as the movie file.

## [0.2.0] - 2026-10-17

### Added

- TMDB client, adding movies and series, root folders, disk scan, import of an existing library.
- Folder browser; root folders that cannot be written are marked.

## [0.1.0] - 2026-10-17

### Added

- Project foundation: SQLite database with migrations, owner sign-in, API key, settings API, log file, the web
  interface shell, Docker image and CI builds for Windows, Linux and macOS.
