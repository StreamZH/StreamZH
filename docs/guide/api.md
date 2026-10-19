# API

Everything the web interface does goes through StreamZH's HTTP API under `/api/v1`, with JSON in and out. Scripts,
dashboards and other tools can use it too.

## Authentication

- **API key**: Settings → General → **API key** (only the owner can see it or create a new one). Send it in the
  `X-Api-Key` header. The API key acts as an **Admin**: it can do everything an Admin can (library, downloads,
  indexers, settings, requests), but not the [owner-only settings](users.md#owner-only-settings).
- **Session cookie** or **Basic** authentication of a user also work. Scripts that change something (POST, PUT,
  PATCH, DELETE) this way must send the header `X-StreamZH-Request: 1`; requests with the API key need no extra header.
- `?apikey=` in the address works only for the live connection `/hub` (WebSocket clients cannot send headers). Keys
  in addresses end up in logs and browser histories; use the header everywhere else.

```bash
curl -H "X-Api-Key: <your API key>" http://localhost:9797/api/v1/system/status
```

With a URL base (Settings → General → Host), put it in front: `http://localhost:9797/streamzh/api/v1/...`.

## OpenAPI

The full description of every endpoint is at **`/api/v1/openapi.json`** (no sign-in needed). Load it into any OpenAPI
tool (Swagger UI, Postman, Insomnia, a client generator) to browse the endpoints and their request and response shapes.

## Examples

```bash
KEY='<your API key>'
URL='http://localhost:9797/api/v1'

# Search TMDB and add a movie to root folder 1, searching for it right away
curl -H "X-Api-Key: $KEY" "$URL/lookup/movies?term=Big%20Buck%20Bunny"
curl -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' \
  -d '{"tmdbId": 10378, "rootFolderId": 1, "monitored": true, "searchForMissing": true}' "$URL/movies"

# All movies, the download queue, the root folders
curl -H "X-Api-Key: $KEY" "$URL/movies"
curl -H "X-Api-Key: $KEY" "$URL/download"
curl -H "X-Api-Key: $KEY" "$URL/rootfolders"

# Start an automatic search for movies 1 and 2
curl -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' \
  -d '{"name": "MoviesSearch", "movieIds": [1, 2]}' "$URL/command"
```

Commands for `POST /api/v1/command`: `MoviesSearch` (`movieIds`), `MissingMoviesSearch`, `SeasonSearch`
(`seriesId`, `seasonNumber`), `EpisodeSearch` (`episodeIds`).

## Other endpoints

| Endpoint | Purpose |
|---|---|
| `GET /health` | `{"status":"ok"}` when StreamZH runs; no sign-in. For health checks and monitoring |
| `/hub` | SignalR live connection: queue progress, activity and status events |
| `GET /api/v1/calendar/feed.ics?token=…` | The iCal feed, with its own read-only token ([Calendar](calendar.md#ical-feed)) |
| `/api/v1/migration/…` | The migration wizard ([MIGRATION.md](../MIGRATION.md#reference-wizard-options-and-api)) |
| Webhook | Not an endpoint: StreamZH sends events to your URL; see [WEBHOOK.md](../WEBHOOK.md) |

Errors come as [problem details](https://www.rfc-editor.org/rfc/rfc9457) JSON; validation errors list the failing
fields by name. Stored secrets are never returned: they read as `********`, and sending `********` back keeps the
stored value as long as the destination (URL, host, port) is unchanged.

The API is not compatible with the Radarr or Sonarr API.
