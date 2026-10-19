# Glossary

| Term | Meaning |
|---|---|
| **Absolute numbering** | Anime episodes numbered through all seasons (`Title - 26`) instead of season and episode |
| **Advanced** | The switch at the top of settings pages that shows expert settings |
| **Alternative title** | Another title of a movie or series (translation, scene name, your own) that releases are matched against |
| **API key** | A secret for scripts and tools, sent as `X-Api-Key`; acts as an Admin |
| **Blackhole folder** | A folder where StreamZH saves grabbed NZB files for another downloader (SABnzbd, NZBGet) |
| **Blocklist** | Releases that failed and are not grabbed again |
| **Category** | A download category (`movies`, `series`, …) with its own folder, priority and post-processing; for torrent clients, the label of StreamZH's torrents |
| **Custom format** | A set of conditions on a release (title, group, language, source, size …) that profiles give a score |
| **Cutoff** | The quality (and optionally custom format score) at which StreamZH stops upgrading |
| **Cutoff unmet** | A file below its profile's cutoff; StreamZH keeps looking for an upgrade |
| **Daily series** | A series whose episodes are named by air date (talk shows, news) |
| **Data folder** | Where StreamZH keeps its database, keys, logs and backups (`C:\ProgramData\StreamZH`, `/var/lib/streamzh`, `/config`) |
| **Delay** | Time a new Usenet or torrent release waits before it may be grabbed, so a better one can show up |
| **Discover** | The page to find and request movies and series from TMDB |
| **Grab** | Sending a chosen release to a download client |
| **Hard link** | A second name for the same file on the same drive: imports without extra space, torrents keep seeding |
| **Import** | Moving (or linking) a finished download into the library and renaming it |
| **Import list** | A list (TMDB, Trakt, Plex watchlist, RSS …) whose titles are added to the library automatically |
| **Indexer** | A site that lists releases: Newznab (Usenet) or Torznab (torrents) |
| **Interactive search** | Manual search: you see all releases and pick one |
| **Minimum availability** | From when a movie counts as released: announced, in cinemas or released |
| **Monitored** | StreamZH looks for and grabs releases for this movie, season or episode |
| **NZB** | A file that lists the Usenet articles of a release |
| **Organize** | Renaming folders and files of library items to the naming scheme, with undo |
| **par2** | Recovery files and the tool that repairs downloads with missing parts |
| **Pending release** | A release waiting for its protocol delay |
| **Profile** | Quality profile: allowed qualities and language, cutoff, custom format scores |
| **Propagation** | The time a new Usenet post needs to reach all servers |
| **Release** | One upload of a movie or episode, named like `Movie.Title.2026.1080p.WEB-DL.x264-GROUP` |
| **Release profile** | Words a release must, must not or should contain |
| **Remote path mapping** | How a path reported by a download client on another machine looks for StreamZH |
| **Requester** | A role that may only use Discover and request |
| **Retention** | How many days a Usenet provider keeps posts |
| **Root folder** | A folder your movies or series live in |
| **RSS sync** | Reading the newest releases of all indexers regularly |
| **Scene numbering** | The episode numbering release groups use, from TheXEM, when it differs from TMDB |
| **Seed ratio, seed time** | How much a torrent must upload, or how long it must seed, after downloading |
| **Setup code** | The one-time code that lets you create the owner account from another device |
| **Side-by-side mode** | StreamZH grabs nothing automatically while the old apps still run (migration) |
| **Status window** | The panel at the bottom right with running work and notices (owners and admins) |
| **Storage location** | A base folder the owner allows StreamZH to use for media and downloads |
| **TMDB** | The Movie Database, where titles, posters and episode lists come from |
| **Torznab, Newznab** | The indexer APIs for torrents and Usenet |
| **TRaSH Guides** | Community guides with tested profiles and custom formats; StreamZH syncs them |
| **URL base** | A path StreamZH is served below, e.g. `/streamzh` behind a reverse proxy |
| **Usenet server** | The news server of your Usenet provider the built-in engine downloads from |
| **Wanted** | Missing items and items below their cutoff |
