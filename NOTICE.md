# Notices

StreamZH is licensed under the GNU General Public License v3.0 only (see `LICENSE`).

## Code taken from other projects

Code ported from the projects below keeps its original copyright header and is listed
here with the source path, so every change can be traced back.

| Project | License | Used for | Files in StreamZH |
|---|---|---|---|
<<<<<<< HEAD
| [Radarr](https://github.com/Radarr/Radarr) | GPL-3.0 | Quality and language definitions (same ids), quality, language and release-group parser, movie release parser, decision rules, Newznab client (caps, requests, RSS parser), release search, parser and indexer test fixtures, custom formats (condition matching, minimum score, upgrades and ranking by score, {Custom Formats} token), alternative titles and translations | `src/StreamZH.Core/Quality/*`, `src/StreamZH.Indexers/Newznab/*`, `src/StreamZH.Library/Search/*`, `tests/StreamZH.Tests/Indexers/*`, `src/StreamZH.Parsing/*Parser.cs`, `src/StreamZH.Parsing/Internal/*`, `src/StreamZH.Parsing/Releases/*`, `src/StreamZH.Core/Decisions/*`, `tests/StreamZH.Tests/Parsing/*`, `src/StreamZH.Core/CustomFormats/*`, `src/StreamZH.Library/CustomFormats/*`, `src/StreamZH.Library/Import/FileNameBuilder.cs`, `src/StreamZH.Host/Api/CustomFormatEndpoints.cs`, `src/StreamZH.Library/Titles/*` |
| [Sonarr](https://github.com/Sonarr/Sonarr) | GPL-3.0 | Episode release parser (SxxEyy, packs, daily, absolute), title normalisation, decision rules, TV search requests, episode/season matching, custom format values for series (release type, sources, languages, indexer flags), scene titles in matching and search (season rules) | `src/StreamZH.Parsing/Releases/*`, `src/StreamZH.Indexers/Newznab/*`, `src/StreamZH.Library/Search/*`, `src/StreamZH.Core/Decisions/*`, `tests/StreamZH.Tests/Parsing/*`, `src/StreamZH.Core/CustomFormats/*`, `src/StreamZH.Library/Titles/*` |
=======
| [Radarr](https://github.com/Radarr/Radarr) | GPL-3.0 | Quality and language definitions (same ids), quality, language and release-group parser, movie release parser, decision rules, Newznab client (caps, requests, RSS parser), release search, parser and indexer test fixtures, custom formats (condition matching, minimum score, upgrades and ranking by score, {Custom Formats} token) | `src/StreamZH.Core/Quality/*`, `src/StreamZH.Indexers/Newznab/*`, `src/StreamZH.Library/Search/*`, `tests/StreamZH.Tests/Indexers/*`, `src/StreamZH.Parsing/*Parser.cs`, `src/StreamZH.Parsing/Internal/*`, `src/StreamZH.Parsing/Releases/*`, `src/StreamZH.Core/Decisions/*`, `tests/StreamZH.Tests/Parsing/*`, `src/StreamZH.Core/CustomFormats/*`, `src/StreamZH.Library/CustomFormats/*`, `src/StreamZH.Library/Import/FileNameBuilder.cs`, `src/StreamZH.Host/Api/CustomFormatEndpoints.cs` |
| [Sonarr](https://github.com/Sonarr/Sonarr) | GPL-3.0 | Episode release parser (SxxEyy, packs, daily, absolute), title normalisation, decision rules, TV search requests, episode/season matching, custom format values for series (release type, sources, languages, indexer flags), series types (daily/anime episode lookup and searches), TheXEM scene numbering and scene names (XemProxy, XemService incl. ExtrapolateMappings) | `src/StreamZH.Parsing/Releases/*`, `src/StreamZH.Indexers/Newznab/*`, `src/StreamZH.Library/Search/*`, `src/StreamZH.Core/Decisions/*`, `tests/StreamZH.Tests/Parsing/*`, `src/StreamZH.Core/CustomFormats/*`, `src/StreamZH.Library/SceneNumbering/*` |
>>>>>>> main
| [TRaSH Guides](https://github.com/TRaSH-Guides/Guides) | MIT | Custom format JSON files and a small subset of the guide JSON with the format index pages as test fixtures (license file kept next to the subset); at runtime the guides are downloaded, not shipped | `tests/StreamZH.Tests/CustomFormats/Files/*`, `tests/StreamZH.Tests/TrashSync/Guides/*` |
| [Prowlarr](https://github.com/Prowlarr/Prowlarr) | GPL-3.0 | Indexer health back-off, Newznab indexer presets, query/grab limits, indexer stats and history, Prowlarr API mapping for the import | `src/StreamZH.Library/Indexers/*` |

## Runtime dependencies

| Component | License |
|---|---|
| .NET / ASP.NET Core | MIT |
| Dapper | Apache-2.0 |
| FluentMigrator | Apache-2.0 |
| Microsoft.Data.Sqlite / SQLite | MIT / Public Domain |
| MailKit, MimeKit (email notifications) | MIT |
| YamlDotNet (Recyclarr configuration import) | MIT |
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

When they are missing, StreamZH downloads the unmodified official release files of these pinned versions at runtime
(SHA-256 checked) into `<data>/tools` on the user's machine, keeps their license files next to them and calls them as
separate programs. Their source code is available from the release pages above. The Docker image uses the Debian packages
`par2` and `7zip` instead.
The PAR 2.0 reader/verifier in `src/StreamZH.Library/PostProcessing` is StreamZH's own code, written from the PAR 2.0 specification.

StreamZH ships no list of indexers. Users add only sources they are permitted to use.
