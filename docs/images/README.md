# Screenshots

The README and the user guide show these screenshots. They are taken from a demo instance with example data only:
the owner "johndoe" and a requester "janedoe", invented movie and series titles, an "Example Indexer" and an
"Example News" server running locally, folders under `/data`, and **generated placeholder posters** (a gradient with
the title). No real posters or artwork, no real indexers, servers, host names or keys.

Size 1440 × 900, dark theme unless noted, PNG.

| File | Page | Used in |
|---|---|---|
| `overview.png` | Overview: a download running and one waiting, the next 7 days, disk space | README |
| `overview-light.png` | The same in the light theme | README, [guide](../guide/README.md) |
| `library-movies.png` | Library → Movies: posters with on-disk, missing and downloading badges | README, [Library](../guide/library.md) |
| `movie-detail.png` | A movie's detail page with its file | [Library](../guide/library.md#the-detail-page) |
| `activity-queue.png` | Activity → Queue: the pipeline strip (grab, download, verify, repair, extract, import) | README, [Downloads](../guide/downloads.md#activity) |
| `discover.png` | Discover: trending rows with library and request states | README, [Requests](../guide/requests.md#discover) |
| `get-started.png` | Get started with four of six steps done | README, [Getting started](../guide/getting-started.md) |
| `settings-profiles.png` | Settings → Profiles with the five default profiles | [Quality](../guide/quality.md#quality-profiles) |
| `migration.png` | Settings → Migration, the first step of the wizard | [MIGRATION.md](../MIGRATION.md#5-run-the-wizard-app-by-app) |

To take them again: run StreamZH with a fresh data folder and `--tmdb-url` pointing at a TMDB stand-in that serves
invented titles, add the example indexer and news server, and capture the pages with Playwright at 1440 × 900.
Serve the placeholder posters for `/api/v1/mediacover/*` from the browser automation instead of TMDB's image CDN.
Check every image for personal data, real artwork and error messages before committing it.
