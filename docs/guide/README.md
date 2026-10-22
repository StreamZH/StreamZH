# StreamZH user guide

Short, task-oriented pages. New here? Start with **Getting started**. Installing is covered in
[INSTALL.md](../INSTALL.md), moving over from Radarr, Sonarr, Prowlarr and SABnzbd in [MIGRATION.md](../MIGRATION.md).

![The Overview: downloads, warnings, the next 7 days and disk space](../images/overview-light.png)

| Page | What it covers |
|---|---|
| [Getting started](getting-started.md) | The first-run checklist: owner account, storage locations, root folders, TMDB key, release language, indexers, download client |
| [Library](library.md) | Movies and series, monitoring, series types (daily, anime), alternative titles, importing existing folders, Organize and renaming, the file renamer |
| [Searching and grabbing](searching.md) | Automatic and interactive search, RSS sync, Wanted, the blocklist, Usenet/torrent preference and delays, the release test |
| [Indexers](indexers.md) | The indexer catalog, Newznab and Torznab, limits, stats and history, import from Prowlarr |
| [Downloads](downloads.md) | The built-in Usenet engine, servers, categories, scheduler, post-processing, torrents (built-in engine and external clients), seeding, remote path mappings, Activity |
| [Quality](quality.md) | Quality profiles, size limits, custom formats, TRaSH Guides sync, release profiles |
| [Requests](requests.md) | Discover, requests and approval, limits, the Discord bot |
| [Users and roles](users.md) | Owner, Admin, Friend, Requester, invites, profiles |
| [Notifications (Connect)](connect.md) | Plex, Jellyfin, Emby, Kodi, Discord, Telegram, email and more; import lists |
| [Calendar](calendar.md) | The calendar and its iCal feed |
| [Settings reference](settings.md) | Every settings page and what each setting does; Advanced ones marked |
| [System](system.md) | Status, tasks, backups, logs, updates, the status window |
| [API](api.md) | API key, OpenAPI, scripts |
| [Troubleshooting and FAQ](troubleshooting.md) | Common problems and their fixes |
| [Glossary](glossary.md) | The words StreamZH uses |

## Conventions

- **Settings → Media Management** means: *Settings* in the sidebar, then the tab *Media Management* in the main
  window (Settings groups its pages in the tabs Media, Downloads, Connections and General). System, Tools, Wanted and
  Indexers have tabs the same way. Ctrl K (Cmd K on a Mac) opens "search or jump to" and finds every page by name.
- **Advanced**: most pages have an *Advanced* switch at the top. Expert settings are hidden until you turn it on;
  they are marked *(Advanced)* in this guide. A hidden setting with an error is always shown.
- **Owner only**: some settings can only be changed by an owner (see [Users and roles](users.md#owner-only-settings)).
  Admins see them read-only.
- Examples use the user **johndoe**, a server at **192.168.1.10**, media in **/data/media** (Linux, Docker) or
  **D:\Media** (Windows). Use your own values.

More reference: [Security](../SECURITY.md), [Webhook payload](../WEBHOOK.md), [Architecture](../ARCHITECTURE.md).
