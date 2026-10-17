# StreamZH Roadmap

Two big steps: a **private test version** that runs next to the existing stack on the
home server, then a **public release** when the repository goes open source.

## Track 1: Private test version (0.x, repo private)

Scope: Movies and Series, Usenet only. Goal: StreamZH can replace Radarr, Sonarr,
SABnzbd and the indexer setup on the CloudZH home server.

| Version | Phase | Content | Done when |
|---|---|---|---|
| 0.1 ✅ | Foundation | Solution, CI, Docker image, config dir, SQLite + migrations, Owner login, API key, settings API, log file, React shell with CloudZH look | Container starts, login works, empty Home page |
| 0.2 ✅ | Metadata & Library | TMDB client, add movie / series, root folders, disk scan, import of an existing library (match folders to TMDB) | Existing movie and TV folders show up in Library |
| 0.3 | Parser & Profiles | Port release-name, quality and language parser incl. test fixtures; quality profiles; decision engine | Parser test suite green, releases ranked correctly |
| 0.4 | Indexers & Search | Newznab client, capabilities, manual search (interactive), automatic search for missing items | Manual search shows ranked releases |
| 0.5 | Download Engine | NZB parser, NNTP pool with backup servers, yEnc, assembly, par2 verify/repair, unpack | A real NZB downloads, repairs and unpacks |
| 0.6 | Import & Activity | Import + rename, history, blocklist + retry, Plex refresh, Activity page with live pipeline | Grab → playable file in Plex without manual steps |
| 0.7 | Automation | RSS sync, calendar, upgrades until cutoff, cleanup, health checks, notifications (Discord) | Runs unattended for two weeks |
| 0.8 | Migration | Import settings and libraries from existing Sonarr/Radarr/Prowlarr/SABnzbd via their APIs | Home server switched over to StreamZH |

### How the test runs on the home server

- Runs as its own Docker container next to the existing apps, on its own port (9797).
- Starts with a separate test library folder, not the live `tv/` and `movies/` folders.
- Usenet connections split with SABnzbd so the provider limit is not exceeded.
- Switch-over only after 0.8, existing apps stay as fallback until then.

## Track 2: Public release (1.0, repo public)

| Block | Content |
|---|---|
| Torrent | Torznab indexers, built-in torrent engine (MonoTorrent, MIT) or qBittorrent bridge, seeding rules |
| More media types | Music (MusicBrainz), Books; Whisparr type optional, hidden by default |
| Discover & Requests | Request flow for Friends, replaces Requestrr; Discord bot |
| Users & roles | Owner, Admin, Friend as in CloudZH |
| Compatibility | Optional Sonarr/Radarr-compatible API endpoints for third-party tools |
| UI | German as second UI language, mobile layout |
| Packaging | Native installers and packages for every platform below, Docker image on GHCR, CloudZH App Store entry |

## Platforms

Like Sonarr and Radarr, StreamZH runs natively on every major platform, not only in
Docker. .NET 10 builds a self-contained app per platform (no .NET install needed).
par2 and 7-Zip are bundled with the native packages so post-processing works out of the box.

| Platform | Delivery | Runs as | Data dir | Available from |
|---|---|---|---|---|
| Linux x64 / arm64 | `.tar.gz` + install script (systemd unit) | systemd service | `/var/lib/streamzh` | 0.1 (CI build), script 0.7 |
| Docker | Image on GHCR (amd64, arm64) | Container | `/config` | 0.1 |
| Windows x64 | Installer (Inno Setup) + portable `.zip` | Windows service or tray app | `C:\ProgramData\StreamZH` | 0.1 (CI build), installer 0.7 |
| macOS (Apple Silicon, Intel) | `.app` in `.zip` | Login item | `~/Library/Application Support/StreamZH` | 1.0 |
| NAS (Synology, QNAP, Unraid, TrueNAS) | Docker image + Unraid template, Synology package later | Container / package | `/config` | 1.0 |
| CloudZH | App Store entry, preconfigured | Container | managed by CloudZH | 1.0 |
| FreeBSD | not planned: .NET has no official FreeBSD support | | | |

The in-app updater (download, verify signature, restart) follows in 1.0 for the native
packages; Docker updates by pulling the new image.

### Checklist before the repository goes public

- [ ] Git history clean: no secrets, no private paths, no home network details
- [ ] `README.md` with screenshots and demo data ("John Doe")
- [ ] `LICENSE` (GPL-3.0), `NOTICE.md` complete for all ported code
- [ ] `CONTRIBUTING.md`, `SECURITY.md`, issue and PR templates
- [ ] Decide CLA yes/no
- [ ] Community channel (GitHub Discussions and/or Discord)
- [ ] Domain and website
- [ ] CI builds and publishes the image on tag
