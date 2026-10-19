# StreamZH Architecture

Status: 1.0 release candidate (first draft 17.10.2026)

StreamZH is one application that replaces the *arr stack: Sonarr, Radarr, Lidarr,
Readarr, Whisparr, Prowlarr, SABnzbd and a torrent client. One login, one database,
one API, one queue. It is built from scratch as its own engine, not as a UI on top of
the existing apps.

## Guiding principles

1. **One process, many modules.** A modular monolith. Modules talk through interfaces
   and an in-process event bus, never through HTTP to each other. One binary, one
   container, one port.
2. **Media types are plugins of the same engine.** Movies and Series first; Music and
   Books later reuse the same pipeline, profiles, indexers and download engine.
3. **Reuse proven GPL-3.0 code where it pays off.** Sonarr, Radarr and Prowlarr are
   GPL-3.0 and already target .NET 10. Their release-name parser, quality parser and
   the test fixtures behind them are the most valuable parts to port. Everything we
   take keeps its copyright header and is listed in `NOTICE.md`.
4. **No bundled indexer list.** Users add their own, permitted sources (Newznab/Torznab
   endpoints). Same rule as CloudZH.
5. **English everywhere** (code, API, CLI, logs, UI, docs; decision 2026-10-19).
6. **Strict separation from CloudZH.** Separate repository. CloudZH talks to StreamZH
   only through the public HTTP API, so the GPL never reaches the CloudZH code.

## Tech stack

| Layer | Choice | Why |
|---|---|---|
| Runtime | .NET 10 (LTS), C# | Same as Sonarr/Radarr/Prowlarr, code can be ported 1:1 |
| Web host | ASP.NET Core minimal APIs | Small, fast, OpenAPI out of the box |
| Live updates | SignalR | Queue progress, activity events, toasts |
| Database | SQLite (default), PostgreSQL later | Zero setup for home users |
| Data access | Dapper + FluentMigrator | Same pattern as the *arr apps, eases porting |
| Logging | Microsoft.Extensions.Logging with a file log in `<data>/logs` | Log viewer under System → Logs |
| Tests | xUnit | Port *arr NUnit fixtures as xUnit theories |
| Frontend | React 19 + TypeScript + Vite | Same family as Sonarr/Radarr UI, largest ecosystem |
| Frontend data | TanStack Query + React Router | Caching, background refresh, routing |
| Styling | CSS modules + design tokens (CloudZH family) | No UI kit lock-in |
| Packaging | Self-contained native packages (Windows, Linux, macOS) + Docker image | Runs natively like Sonarr/Radarr, or in a container; see Platforms in ROADMAP.md |

External tools called as processes (not linked): `par2` (par2cmdline-turbo) for
repair and 7-Zip for extraction. Same approach as SABnzbd. When they are missing, StreamZH
installs pinned official releases (SHA-256 checked) into `<data>/tools` itself
(`ToolInstaller`, Settings → Downloads → Tools).

## Module map

```
                         ┌──────────────────────────────┐
  Browser / CloudZH ───▶ │ StreamZH.Host                │  HTTP API /api/v1, SignalR /hub,
                         │ (API, auth, scheduler, UI)   │  serves the React build
                         └──────────────┬───────────────┘
                                        │ commands / queries / events
   ┌──────────────┬──────────────┬──────┴───────┬──────────────┬──────────────┐
   ▼              ▼              ▼              ▼              ▼              ▼
 Library       Metadata       Indexers       Parsing        Usenet         Data
 (movies,      (TMDB)         (Newznab,      (release       (NZB, NNTP,    (SQLite,
 series,                      search,        names,         yEnc, par2,    migrations,
 import,                      RSS)           quality)       unpack)        repositories)
 rename)
   └──────────────────────────── StreamZH.Core ────────────────────────────────┘
        domain model, profiles, decision engine, pipeline, event bus, contracts
```

| Project | Responsibility |
|---|---|
| `StreamZH.Core` | Domain model, profiles, decision engine, pipeline state machine, event bus, module contracts |
| `StreamZH.Data` | Database, migrations, repositories |
| `StreamZH.Parsing` | Release-name parser, quality parser, language parser (port from Sonarr/Radarr) |
| `StreamZH.Metadata` | TMDB client for movies and series, artwork cache |
| `StreamZH.Indexers` | Newznab and Torznab clients, search, RSS sync, rate limits, capabilities |
| `StreamZH.Usenet` | Download engine: NZB parser, NNTP connection pool, yEnc decoder, file assembly, par2, unpack |
| `StreamZH.Torrent` | Built-in torrent engine (MonoTorrent): the "StreamZH" torrent client, its settings, fast resume and seed goals |
| `StreamZH.Library` | Root folders, disk scan, import, rename, media server notifications (Plex first) |
| `StreamZH.Host` | API, authentication, background scheduler, SignalR, static frontend |
| `StreamZH.Tray` | Windows only: `StreamZH.Tray.exe`, the notification area icon (WinForms); talks to the host through `/api/v1/tray` with the token from `<data>/tray.token` |
| `StreamZH.Tray.Core` | The tray's logic without UI (status text, state, notifications, files, HTTP client), tested on every OS |

## The pipeline

Every download, no matter the media type, walks the same state machine. This is what
the Activity page shows.

```
Search ─▶ Grab ─▶ Download ─▶ Verify & Unpack ─▶ Import & Rename ─▶ Notify
  │         │        │               │                   │
  └─ no     └─ NZB   └─ failed ─────▶└─ failed ─────────▶└─ Blocklist release,
     result    fetch    (missing        (par2 cannot         search next best
                        articles)       repair)
```

- One job table for all media types. Each job has a stage, progress, history and a link
  to the wanted item (movie or episode set).
- Failed jobs blocklist the release and trigger the next search automatically.
- Because the download engine is built in, there is no polling of an external client
  and no "completed download handling" guessing: the engine hands the finished folder
  directly to the import step.

## Domain model (first cut)

```
MediaItem (abstract)        Movie, Series ─< Season ─< Episode
  ├─ monitored                 (Music: Artist ─< Album ─< Track, later)
  ├─ qualityProfileId          (Books: Author ─< Book, later)
  ├─ rootFolderId
  └─ tmdbId / externalIds

QualityProfile ── one rule set for all media types (allowed qualities, cutoff,
                  custom formats, preferred words, size limits per type)
Indexer ── url, apiKey, categories per media type, priority, enabled for search/RSS
UsenetServer ── host, port, ssl, username, password, connections, priority (backup servers)
Release ── parsed result of an indexer item (title, quality, size, age, indexer)
Job ── pipeline instance (stage, progress, release, target items, history)
HistoryEvent ── grabbed, downloaded, imported, failed, deleted, renamed
User ── Owner, Admin, Friend (same roles as CloudZH)
```

## Download engine (Usenet)

1. **NZB parser**: reads files, segments and groups. Detects par2 sets and archives.
2. **NNTP pool**: one pool per server, connection limit per server, TLS, `BODY` per
   article, retry on next server (backup servers by priority). Per-server expert settings:
   timeout, certificate check, pipelining (articles per request), required/optional
   (pause downloads vs. skip longer on failure), download quota, account expiry notice.
3. **yEnc decoder**: decodes article bodies, checks CRC32 per part. Hot path, written
   with `Span<byte>`; SIMD later if needed.
4. **Assembler**: writes decoded parts at their offset into the target file (no temp
   copy per article).
5. **Post-processing**: `par2` verify, repair only if needed (downloads extra par2
   blocks on demand), unpack (`unrar`/`7z`), cleanup, hand over to import.
6. **Speed and disk**: global speed limit, pause/resume, free-space guard before grab.

## API

- REST under `/api/v1/...`, JSON, OpenAPI document at `/api/v1/openapi.json`.
- Auth: cookie session for the web UI (Forms), or HTTP Basic, or External (a reverse
  proxy signs in); API key header `X-Api-Key` for scripts and CloudZH. Optionally no
  sign-in from local addresses (Settings → General → Security). `--auth-method forms`
  on the command line overrides the stored method (way back in after a lockout).
- SignalR hub `/hub` pushes queue progress and events.
- Compatibility layer (later, optional): minimal Sonarr/Radarr-compatible endpoints so
  tools like Requestrr, Overseerr/Seerr or Plex helpers keep working.

## Runtime layout

| Item | Default |
|---|---|
| Port | 9797 (Settings → General → Host: bind address, port, URL base, HTTPS; `--urls` or `ASPNETCORE_URLS` win) |
| Config/data dir | `/config` in Docker, `C:\ProgramData\StreamZH` on Windows, `~/Library/Application Support/StreamZH` on macOS, `~/.config/streamzh` on Linux (`/var/lib/streamzh` as systemd service via `STREAMZH_DATA`) |
| Database | `<data>/streamzh.db` |
| Logs | `<data>/logs/` (1 MB per file, 5 old files; configurable) |
| Backups | `<data>/Backups/scheduled` and `manual` (zip: database via `VACUUM INTO`, keys, backup.json) |
| Staged restore | `<data>/restore/pending.zip`, applied at the next start before the database opens; the old database stays in `<data>/restore/before-restore-*` |
| Incomplete downloads | `<data>/downloads/incomplete` (configurable) |

## Security

- First start: create the Owner account, no default password. From another device than this computer the setup
  page needs the one-time setup code from the console/log, `<data>/setup-code` or the tray icon (0.12).
- 0.12 hardening (see docs/SECURITY.md): cross-site request protection (`Security/CrossSiteProtection`), DNS
  rebinding protection for the sign-in-free modes (`Security/HostAccess`), trusted proxies and local networks
  (`Security/AccessSettings`, `Security/ProxyForwarding`), server-side sessions (`Sessions` table,
  `Auth/SessionStore`), sign-in throttling (`Security/LoginThrottle`), live connections that close when their
  sign-in ends (`Hubs/HubConnections`), security headers and CSP (`Security/SecurityHeaders`).
- Passwords hashed with PBKDF2 (ASP.NET Core Identity hasher).
- Secrets (indexer keys, Usenet passwords) never returned by the API in clear text,
  masked in the UI and in logs.
- No telemetry.
- Outgoing HTTP (all `IHttpClientFactory` clients) goes through the proxy of Settings →
  General and its certificate-validation choice; NNTP is never proxied.
- Restart (Settings → General, System → Status) runs the whole startup again in the
  same process (`goto start` in Program.cs), so it works in a console, as a service and
  in Docker without a supervisor; a new port that cannot be bound falls back to the
  previous one.
