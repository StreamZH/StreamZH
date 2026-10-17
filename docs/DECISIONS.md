# Decisions

Short log of decisions that shape the project. Newest first.

| Date | Decision | Reason |
|---|---|---|
| 2026-10-17 | TMDB is the single metadata source for movies and series; each user brings their own free TMDB key | One source for both types, no proxy server to run; TVDB id is still stored for compatibility |
| 2026-10-17 | Removing an item from the library never deletes files; file deletion becomes an explicit option later | Safe default while the app is young |
| 2026-10-17 | Posters load directly from the TMDB image CDN; local image cache later | Keeps 0.2 small; cache needed for offline use and privacy |
| 2026-10-17 | Library import keeps existing folder names, nothing is moved or renamed | Lets users try StreamZH on a live library without risk |
| 2026-10-17 | Native packages for Windows, Linux and macOS like Sonarr/Radarr, plus Docker; no FreeBSD | Most home users run Windows or a NAS, not only Docker |
| 2026-10-17 | First test version: Movies + Series, Usenet only | Replaces the stack that runs on the home server today (Radarr, Sonarr, SABnzbd); fastest path to a real test |
| 2026-10-17 | Frontend: React + TypeScript + Vite | Largest ecosystem, same family as the Sonarr/Radarr UI |
| 2026-10-17 | Backend: .NET 10 / C# | Sonarr, Radarr and Prowlarr are GPL-3.0 on .NET 10, their parser code and tests can be ported directly |
| 2026-10-17 | Own engine built from scratch, not a UI over the existing *arr APIs | One database, one queue, built-in download engine without polling |
| 2026-10-17 | Repository private during the test phase, public from 1.0 | Build in peace, clean up history before release |
| 2026-10-17 | License GPL-3.0-only | Allows reuse of *arr code; others may build on it, also as a hosted service |
| 2026-10-17 | Separate repository from CloudZH, integration only through the HTTP API | Keeps the GPL away from the commercial CloudZH code |
| 2026-10-17 | Data access: Dapper + FluentMigrator, SQLite by default | Same pattern as the *arr apps, simplifies porting |
| 2026-10-17 | Default port 9797, data dir `/config` in Docker | Does not collide with Sonarr (8989), Radarr (7878), Prowlarr (9696), SABnzbd (8080) |
