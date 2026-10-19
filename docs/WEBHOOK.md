# Webhook payload

Settings → Connect → Webhook sends every event as JSON (`POST` or `PUT`, optional basic authentication and extra
headers). The layout follows the Radarr and Sonarr webhooks, so most scripts written for them work unchanged.
A movie event has `movie` and `movieFile`; an episode event has `series`, `episodes` and `episodeFiles`.
Fields without a value are `null` or left out.

## Common fields

| Field | Type | Notes |
|---|---|---|
| `eventType` | string | `Grab`, `Download` (import and upgrade), `Rename`, `DownloadFailure`, `Health`, `HealthRestored`, `Test`; requests: `RequestCreated`, `RequestApproved`, `RequestDeclined`, `RequestAvailable` |
| `instanceName` | string | Settings → General → instance name |
| `applicationName` | string | `StreamZH` |
| `applicationVersion` | string | e.g. `0.6.0` |

## Media

```json
"movie": {
  "id": 3, "title": "Dune", "year": 2021, "folderPath": "/media/movies/Dune (2021)",
  "tmdbId": 438631, "imdbId": "tt1160419", "overview": "…", "genres": ["Science Fiction"],
  "images": [{ "coverType": "poster", "remoteUrl": "https://image.tmdb.org/t/p/w500/…jpg" },
             { "coverType": "fanart", "remoteUrl": "https://image.tmdb.org/t/p/w1280/…jpg" }]
}
```

`series` has the same fields, with `path` instead of `folderPath` and an extra `tvdbId`.

```json
"episodes": [{ "id": 41, "seasonNumber": 1, "episodeNumber": 2, "title": "Cat's in the Bag...", "airDate": "2008-01-27", "overview": null }]
```

## Release and files

```json
"release": { "releaseTitle": "Dune.2021.1080p.WEB-DL.DDP5.1.H.264-FLUX", "quality": "WEBDL-1080p",
             "releaseGroup": "FLUX", "indexer": "NZBgeek", "size": 5000000000 },
"movieFile": { "path": "/media/movies/Dune (2021)/Dune (2021).mkv", "relativePath": "Dune (2021).mkv", "size": 5000000000,
               "quality": "WEBDL-1080p", "releaseGroup": "FLUX", "sceneName": "Dune.2021.1080p.WEB-DL.DDP5.1.H.264-FLUX" },
"isUpgrade": false,
"downloadId": "12"
```

`episodeFiles` is a list of the same objects as `movieFile`. `indexer` is only known for grabs and failed downloads.

## Per event

| eventType | Contains |
|---|---|
| `Grab` | media, `episodes`, `release` (with `indexer`) |
| `Download` | media, `episodes`, `release`, `movieFile` or `episodeFiles`, `isUpgrade`, `downloadId` |
| `Rename` | media (the folder is `folderPath` / `path`) |
| `DownloadFailure` | media when known, `episodes`, `release`, `downloadId`, `message` (the error) |
| `Health`, `HealthRestored` | `level` (`warning` or `error`), `message`, `type` (the notice key, e.g. `no-indexer`), `wikiUrl` (`null`) |
| `Test` | a sample movie (Big Buck Bunny) with a sample release |
| `RequestCreated`, `RequestApproved`, `RequestDeclined`, `RequestAvailable` | media (the library item once it was added, else `id` is `null`), `request`: `id`, `status` (`pending`, `approved`, `declined`, `processing`, `partiallyAvailable`, `available`, `failed`), `requestedBy` (display name; a Discord guest as "name (Discord)"), `source` (`web`, `discord`, `api`), `seasons` (numbers, `null` = all seasons or a movie), `note`, `reason` (why it was declined), `automatic` (approved at once when it was created) |

A failing webhook (network error, timeout, HTTP 429 or 5xx) is retried up to three times within about six minutes;
other errors are not retried. The last error shows in Settings → Connect and in the status window.
