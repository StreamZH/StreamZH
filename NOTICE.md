# Notices

StreamZH is licensed under the GNU General Public License v3.0 only (see `LICENSE`).

## Code taken from other projects

Code ported from the projects below keeps its original copyright header and is listed
here with the source path, so every change can be traced back.

| Project | License | Used for | Files in StreamZH |
|---|---|---|---|
| [Radarr](https://github.com/Radarr/Radarr) | GPL-3.0 | Quality and language definitions (same ids), quality, language and release-group parser, movie release parser, decision rules, Newznab client (caps, requests, RSS parser), release search, parser and indexer test fixtures, custom formats (condition matching, minimum score, upgrades and ranking by score, {Custom Formats} token), alternative titles and translations, media info (ffprobe analysis: codec, HDR and channel formatting, resolution from the video, {MediaInfo …} tokens and language filters, "Not a Custom Format upgrade" on import) | `src/StreamZH.Core/Quality/*`, `src/StreamZH.Indexers/Newznab/*`, `src/StreamZH.Library/Search/*`, `tests/StreamZH.Tests/Indexers/*`, `src/StreamZH.Parsing/*Parser.cs`, `src/StreamZH.Parsing/Internal/*`, `src/StreamZH.Parsing/Releases/*`, `src/StreamZH.Core/Decisions/*`, `tests/StreamZH.Tests/Parsing/*`, `src/StreamZH.Core/CustomFormats/*`, `src/StreamZH.Library/CustomFormats/*`, `src/StreamZH.Library/Import/FileNameBuilder.cs`, `src/StreamZH.Host/Api/CustomFormatEndpoints.cs`, `src/StreamZH.Library/Titles/*`, `src/StreamZH.Core/Media/MediaInfo.cs`, `src/StreamZH.Library/MediaFiles/*`, `src/StreamZH.Library/Import/DownloadImportService.cs` |
| [Sonarr](https://github.com/Sonarr/Sonarr) | GPL-3.0 | Episode release parser (SxxEyy, packs, daily, absolute), title normalisation, decision rules, TV search requests, episode/season matching, custom format values for series (release type, sources, languages, indexer flags), series types (daily/anime episode lookup and searches), TheXEM scene numbering and scene names (XemProxy, XemService incl. ExtrapolateMappings), scene titles in matching and search (season rules), title lengths in file names, Sonarr's quality ids and sources for the 0.9 migration | `src/StreamZH.Library/Migration/Arr/Settings/ArrQualities.cs`, `src/StreamZH.Parsing/Releases/*`, `src/StreamZH.Indexers/Newznab/*`, `src/StreamZH.Library/Search/*`, `src/StreamZH.Core/Decisions/*`, `tests/StreamZH.Tests/Parsing/*`, `src/StreamZH.Core/CustomFormats/*`, `src/StreamZH.Library/SceneNumbering/*`, `src/StreamZH.Library/Titles/*` |
| [Radarr](https://github.com/Radarr/Radarr), [Sonarr](https://github.com/Sonarr/Sonarr) | GPL-3.0 | Migration (0.9): the shapes of their API v3 resources (movie, movie file, series, episode, episode file, media info, history, blocklist, paging), their history event names, quality names and media info strings (HDR types, run time), read when taking over a library | `src/StreamZH.Library/Migration/Arr/Library/ArrLibraryResources.cs`, `src/StreamZH.Library/Migration/Arr/Library/ArrLibraryMapping.cs` |
| [Radarr](https://github.com/Radarr/Radarr) | GPL-3.0 | Torrent download clients (0.11): the qBittorrent WebUI API v2, Transmission RPC and Deluge JSON-RPC proxies (logins and session handling, add with category/labels and seed limits, torrent states, remove), download client settings fields, remote path mappings, removing completed torrents once seeded | `src/StreamZH.Library/Downloads/Torrent/*`, `src/StreamZH.Library/Search/GrabService.cs` |
| [Radarr](https://github.com/Radarr/Radarr), [Sonarr](https://github.com/Sonarr/Sonarr), [Prowlarr](https://github.com/Prowlarr/Prowlarr) | GPL-3.0 | Torrents (0.11): Torznab RSS parser (TorznabRssParser/TorrentRssParser: enclosure and magnet links, seeders, peers or seeders + leechers, info hash, volume factors and tags to indexer flags, Prowlarr's minimum ratio/seed time and tags), Torznab test (Jackett "all" endpoint, unsupported categories), minimum seeders (TorrentSeedingSpecification), protocol delay and pending releases (DelaySpecification, PendingReleaseService, simplified to one delay profile), protocol and seeders ranking (DownloadDecisionComparer), seed goals (SeedConfigProvider), Torznab presets, the Torznab seed fields of their indexer API for the import | `src/StreamZH.Indexers/Torznab/*`, `src/StreamZH.Core/Indexers/IndexerFlagBits.cs`, `src/StreamZH.Core/Decisions/Specifications/TorrentSeedingSpecification.cs`, `src/StreamZH.Core/Decisions/Specifications/DelaySpecification.cs`, `src/StreamZH.Core/Decisions/DecisionComparer.cs`, `src/StreamZH.Library/Search/PendingReleases.cs`, `src/StreamZH.Library/Search/SeedCriteria.cs`, `src/StreamZH.Library/Indexers/*`, `src/StreamZH.Library/Migration/Indexers/ArrIndexersSection.cs`, `tests/StreamZH.Tests/Indexers/*` |
| [TRaSH Guides](https://github.com/TRaSH-Guides/Guides) | MIT | Custom format JSON files and a small subset of the guide JSON with the format index pages as test fixtures (license file kept next to the subset); at runtime the guides are downloaded, not shipped | `tests/StreamZH.Tests/CustomFormats/Files/*`, `tests/StreamZH.Tests/TrashSync/Guides/*` |
| [Overseerr](https://github.com/sct/overseerr) | MIT | Requests (0.10): the request lifecycle and statuses, per-season duplicate checks and request quotas follow its request service (flow and rules, no code copied) | `src/StreamZH.Library/Requests/RequestService.cs` |
| [Prowlarr](https://github.com/Prowlarr/Prowlarr) | GPL-3.0 | Indexer health back-off, Newznab indexer presets, query/grab limits, indexer stats and history, Prowlarr API mapping for the import | `src/StreamZH.Library/Indexers/*` |
| [Requestrr](https://github.com/thomst08/requestrr) | GPL-3.0 | Discord request flow (search → pick a title → pick seasons → request; channel and role restrictions; TV season restrictions; notifying requesters) reimplemented for the built-in bot, and the shape of `config/settings.json` for the settings import (0.10) | `src/StreamZH.Library/Discord/DiscordInteractionHandler.cs`, `src/StreamZH.Library/Discord/RequestrrImport.cs` |

## Runtime dependencies

| Component | License |
|---|---|
| .NET / ASP.NET Core | MIT |
| Dapper | Apache-2.0 |
| FluentMigrator | Apache-2.0 |
| Microsoft.Data.Sqlite / SQLite | MIT / Public Domain |
| MailKit, MimeKit (email notifications) | MIT |
| YamlDotNet (Recyclarr configuration import) | MIT |
| [MonoTorrent](https://github.com/alanmcgovern/monotorrent) 3.0.2 (built-in torrent engine, `src/StreamZH.Torrent`), with its dependencies Mono.Nat 3.0.0 and ReusableTasks 4.0.0 | MIT (license texts in `src/StreamZH.Torrent/licenses`, shipped as `licenses/` next to StreamZH) |
| BouncyCastle.Cryptography (used by MimeKit) | MIT |
| React, React Router, TanStack Query | MIT |
| @microsoft/signalr | MIT |
| Geist, Geist Mono | SIL Open Font License 1.1 |
| Instrument Serif | SIL Open Font License 1.1 |
| Outfit (logo wordmark, outlined to paths; font files only in `tools/brand/fonts`) | SIL Open Font License 1.1 |

External tools called as separate processes (not linked, not part of StreamZH, not shipped in source):

| Tool | License | Source |
|---|---|---|
| [par2cmdline-turbo](https://github.com/animetosho/par2cmdline-turbo) 1.5.0 | GPL-2.0-or-later | https://github.com/animetosho/par2cmdline-turbo/releases |
| [7-Zip](https://www.7-zip.org) 26.03 | LGPL-2.1-or-later; RAR code under the unRAR license restriction | https://github.com/ip7z/7zip/releases |
| [FFmpeg](https://ffmpeg.org) 9.0.2 (ffprobe from the gyan.dev "essentials" build, Windows x64) | GPL-3.0-or-later (this build) | https://github.com/GyanD/codexffmpeg/releases (source commit named in the README.txt kept next to ffprobe.exe; FFmpeg source at https://ffmpeg.org/download.html) |

When they are missing, StreamZH downloads the unmodified official release files of these pinned versions at runtime
(SHA-256 checked) into `<data>/tools` on the user's machine, keeps their license files next to them and calls them as
separate programs. Their source code is available from the release pages above. Of the FFmpeg build only `ffprobe.exe`,
`LICENSE` and `README.txt` are unpacked; on Linux and macOS ffprobe comes from the system's `ffmpeg` package. The Docker
image uses the Debian packages `par2`, `7zip` and `ffmpeg` instead.
The PAR 2.0 reader/verifier in `src/StreamZH.Library/PostProcessing` is StreamZH's own code, written from the PAR 2.0 specification.

StreamZH ships no list of indexers. Users add only sources they are permitted to use.
