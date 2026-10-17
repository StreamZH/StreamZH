# Notices

StreamZH is licensed under the GNU General Public License v3.0 only (see `LICENSE`).

## Code taken from other projects

Code ported from the projects below keeps its original copyright header and is listed
here with the source path, so every change can be traced back.

| Project | License | Used for | Files in StreamZH |
|---|---|---|---|
| [Sonarr](https://github.com/Sonarr/Sonarr) | GPL-3.0 | planned: release-name, quality and language parser, parser test fixtures | none yet |
| [Radarr](https://github.com/Radarr/Radarr) | GPL-3.0 | planned: movie parser, parser test fixtures | none yet |
| [Prowlarr](https://github.com/Prowlarr/Prowlarr) | GPL-3.0 | planned: Newznab capabilities handling | none yet |

## Runtime dependencies

| Component | License |
|---|---|
| .NET / ASP.NET Core | MIT |
| Dapper | Apache-2.0 |
| FluentMigrator | Apache-2.0 |
| Microsoft.Data.Sqlite / SQLite | MIT / Public Domain |
| React, React Router, TanStack Query | MIT |
| @microsoft/signalr | MIT |
| Geist, Geist Mono | SIL Open Font License 1.1 |
| Instrument Serif | SIL Open Font License 1.1 |

External tools called as separate processes (not linked, not shipped in source):
par2cmdline (GPL-2.0-or-later), 7-Zip (LGPL-2.1 with unRAR restriction).

StreamZH ships no list of indexers. Users add only sources they are permitted to use.
