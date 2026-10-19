# StreamZH Roadmap

StreamZH replaces Radarr, Sonarr, Prowlarr, SABnzbd, a torrent client, Recyclarr and Requestrr with one application.
This page shows where it stands and what comes next. Plans change; the [issues](https://github.com/streamzh/streamzh/issues)
are the place to discuss them. What changed in each version is in the [changelog](../CHANGELOG.md).

## Where StreamZH stands: 1.0 release candidate

Movies and series, from search to a playable file, with Usenet and torrents:

| Version | Content |
|---|---|
| 0.1 – 0.3 | Foundation, TMDB library, import of existing folders, release parser, quality profiles, decision engine |
| 0.4 | Newznab indexers, search, and Prowlarr built in (catalog, limits, stats, history, import) |
| 0.5 | Built-in Usenet engine: download, par2 repair, unpack; Activity and the status window |
| 0.6 | Import and rename, blocklist, Connect (media servers and notifications), settings like Radarr, Sonarr and SABnzbd, System pages |
| 0.7 | Wanted, calendar and iCal, mass editor and Organize, file renamer, import lists, Windows and systemd services |
| 0.8 | Custom formats, TRaSH Guides sync, Recyclarr import, Windows tray icon; alternative titles, daily and anime series, media info |
| 0.9 | Migration wizard for Radarr, Sonarr, Prowlarr and SABnzbd, side-by-side mode |
| 0.10 | Users and roles, invites, Discover, requests, built-in Discord bot |
| 0.11 | Torrents: Torznab, qBittorrent, Transmission, Deluge, built-in torrent engine, seeding |
| 0.12 | Security review: setup code, storage locations, owner-only settings, encrypted secrets, browser protection |
| 1.0 RC | Installers and packages for every platform, first-run guidance, complete documentation |

## Platforms

StreamZH runs natively on every major platform, not only in Docker. The native packages are self-contained (no .NET
install needed); par2 and 7-Zip are installed by StreamZH at the first start.

| Platform | Delivery | Runs as | Data folder |
|---|---|---|---|
| Windows x64 | Installer and portable zip | Windows service with tray icon, or tray icon only | `C:\ProgramData\StreamZH` |
| Linux x64, arm64 | `.deb`, `.rpm`, `.tar.gz` with install script | systemd service | `/var/lib/streamzh` |
| Docker (amd64, arm64) | Image on GHCR | Container | `/config` |
| macOS (Apple silicon, Intel) | zip, not notarized yet | Program | `~/Library/Application Support/StreamZH` |
| FreeBSD | not planned: .NET has no official FreeBSD support | | |

## Next

Not promised, roughly in order:

| Topic | Content |
|---|---|
| Updates | An in-app updater for the native packages (download, verify signature, restart); signed Windows and macOS builds |
| NAS | Unraid template and other NAS app store entries; a Synology package later |
| UI | Mobile layout polish (the interface stays English only) |
| More media types | Music (MusicBrainz) and books on the same engine |
| Compatibility | Optional Sonarr/Radarr-compatible API endpoints for third-party tools |
| Database | PostgreSQL as an alternative to SQLite for large installations |
| CloudZH | An App Store entry in CloudZH, preconfigured |

## Before the repository goes public

- [ ] Git history clean: no secrets, no private paths, no home network details
- [x] `README.md` with screenshots and demo data ("John Doe"), see [docs/images](images/README.md)
- [ ] `LICENSE` (GPL-3.0), `NOTICE.md` complete for all ported code
- [x] `CONTRIBUTING.md`, `SECURITY.md`, issue and PR templates
- [x] `CODE_OF_CONDUCT.md`, `CHANGELOG.md`, user guide and installation guide
- [ ] Decide CLA yes/no
- [ ] Community channel (GitHub Discussions and/or Discord)
- [ ] Domain and website
- [x] CI builds and publishes the image and the packages on tag (`release.yml`; first real run with `v1.0.0-rc.1`)
- [x] Repository owner decided: the GitHub organization StreamZH (`streamzh/streamzh`)
