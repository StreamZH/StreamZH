# Decisions

Short log of decisions that shape the project. Newest first.

| Date | Decision | Reason |
|---|---|---|
| 2026-10-17 | Advanced mode like Sonarr/SABnzbd: simple view by default, one "Advanced" switch (remembered per browser) shows the expert settings, their labels in the warn colour; a save error on a hidden field switches it on | Most users never need timeouts or pipelining; experts find everything in one place without a separate settings page |
| 2026-10-17 | Usenet server "On failure" is one select: Skip (default, 5 min), Pause downloads (required: every fetch fails while the server is in back-off, its quota is used up or it failed for an article, so the engine pauses and nothing is marked missing), Ignore longer (optional: 30 min back-off, no status-window notice) | Same idea as SABnzbd's required/optional, but one choice instead of two exclusive checkboxes |
| 2026-10-17 | Articles per request (pipelining) defaults to 1, not SABnzbd's 2 | New pipelining code in the NNTP client; experts can raise it per server |
| 2026-10-17 | Server quota counts NNTP bytes in GB of 10^9 (as providers sell them) from when it is set or reset; used up = server skipped and a notice | A block account must not be overdrawn; the free-space setting stays in GB of 1024³ like the disk notice |
| 2026-10-17 | Download engine expert settings: minimum free space (1 GB), abort hopeless downloads (on), keep par2/archive files (off), tries per article (3) | The engine's former constants; defaults keep the old behaviour |
| 2026-10-17 | Built-in Usenet download engine is the default download client; "Blackhole folder" stays as an option for an external downloader | StreamZH replaces SABnzbd; the folder mode keeps SABnzbd usable during the switch |
| 2026-10-17 | par2 verify and deobfuscation in own C# code; repair with par2cmdline and unpacking with 7-Zip as external processes (par2 downloaded by Update-Local.ps1 on Windows) | Verify is needed for every download and must not depend on tools; repair and RAR unpacking are best left to the proven tools, not linked |
| 2026-10-17 | Recovery par2 volumes are only fetched when articles are missing (about 1.2× the missing data, all of them if repair still lacks blocks) | Saves bandwidth like SABnzbd |
| 2026-10-17 | Status window bottom right like CloudZH (running tasks + heads-up notices), Activity page with a pipeline per download | Owner wants to see at a glance what is happening |
| 2026-10-17 | Logo: letter corners of the wordmark softened (radius 1.5), gap reduced by a quarter to 0.24 | Owner's request: less angular, monogram and name closer together |
| 2026-10-17 | Logo refined: wordmark 70 % of the monogram, gap 0.32, monogram corners softened (radius 1.5), app icon monogram 60 % | Owner's choice after comparing variants; reads better next to the monogram and less angular |
| 2026-10-17 | Indexer catalog like Prowlarr: StreamZH ships indexer definitions (Usenet Newznab presets now, torrent definitions with the torrent engine in 1.0); replaces the earlier "no built-in indexer list" rule | Owner decision: adding an indexer should be as easy as in Prowlarr, Prowlarr no longer needed |
| 2026-10-17 | Prowlarr features built in: per-indexer query/grab limits, VIP expiry, additional parameters, indexer history, stats, search across all indexers, import from Prowlarr | StreamZH replaces Prowlarr completely |
| 2026-10-17 | Until the built-in download engine (0.5), "Grab" saves the NZB into a folder that SABnzbd can watch | Real downloads can be tested with 0.4 already; the folder becomes the engine's input later |
| 2026-10-17 | Failing indexers pause with escalating back-off (5 min up to 24 h), a success resets it | Same idea as Prowlarr; protects API limits |
| 2026-10-17 | Releases without a language tag count as the original language of the movie or series (from TMDB) | Same as Radarr; English scene releases carry no tag |
| 2026-10-17 | Ranking: quality first, then PROPER/REPACK, then dual language | Same order as Radarr without custom formats; a German.DL WEB-1080p does not beat a German Bluray-1080p |
| 2026-10-17 | A file in the wrong language is always replaced by a release in the profile language, even past the cutoff | A GermanSubbed or English file in a "Deutsch" profile is not finished |
| 2026-10-17 | Release parser: Sonarr episode patterns and Radarr movie patterns in one parser; clear SxxEyy wins, ambiguous patterns lose to a movie | Indexer searches mix both; names like "Blade.Runner.2049.2017" must stay movies |
| 2026-10-17 | Logo: same as CloudZH (monogram Z in front of H, Zurich red #C0202A, Outfit wordmark) with «Stream» in place of «Cloud» | StreamZH is part of the CloudZH family; one brand system, generator in `tools/brand` |
| 2026-10-17 | Quality and language ids identical to Radarr | Profiles and history can be imported from Radarr/Sonarr later (0.8) |
| 2026-10-17 | One quality profile model for all media types, with required language and "prefer dual language" | German scene releases (German.DL) are the main use case of the first user |
| 2026-10-17 | Default profile "Deutsch 1080p" in the test phase | Matches the owner's library; public 1.0 will default to "HD-1080p" with original language |
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
