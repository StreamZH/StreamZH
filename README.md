<p align="center">
  <img src="frontend/public/favicon.svg" width="64" height="64" alt="StreamZH">
</p>

<h1 align="center">StreamZH</h1>

<p align="center">One app for your whole media pipeline.</p>

> **Status:** early development (0.1, private test phase). Not ready for use.

StreamZH replaces the *arr stack with a single application: one login, one database,
one API, one queue.

| Today | In StreamZH |
|---|---|
| Sonarr, Radarr (Lidarr, Readarr later) | **Library** |
| Prowlarr | **Indexers** |
| SABnzbd (torrent client later) | **Download Engine** |
| Five activity tabs | **Activity**: Search → Grab → Download → Verify & Unpack → Import & Rename → Notify |
| Five sets of quality profiles | **Profiles**: one rule set for all media types |

StreamZH ships no list of indexers. Add only sources you are permitted to use.

## Development

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download), Node.js 22 or newer.

```bash
# Frontend (builds into src/StreamZH.Host/wwwroot)
cd frontend
npm install
npm run build
cd ..

# Backend on http://localhost:9797, data in ./.devdata
dotnet run --project src/StreamZH.Host -- --data .devdata

# Tests
dotnet test
```

For UI work run `npm run dev` in `frontend/` (http://localhost:5173). It proxies
`/api` and `/hub` to the backend on port 9797.

### Docker

```bash
docker build -f docker/Dockerfile -t streamzh:dev .
docker run -d --name streamzh -p 9797:9797 -v /opt/streamzh:/config streamzh:dev
```

## Repository layout

| Path | Content |
|---|---|
| `src/StreamZH.Host` | ASP.NET Core host: API, SignalR hub, serves the web UI |
| `src/StreamZH.Core` | Domain model, pipeline, event bus |
| `src/StreamZH.Data` | SQLite database, migrations, repositories |
| `src/StreamZH.Parsing` | Release-name and quality parser |
| `src/StreamZH.Metadata` | TMDB client |
| `src/StreamZH.Indexers` | Newznab client, search, RSS |
| `src/StreamZH.Usenet` | Download engine: NZB, NNTP, yEnc, par2, unpack |
| `src/StreamZH.Library` | Import, rename, disk scan, media server notifications |
| `frontend/` | React + TypeScript web UI |
| `tests/` | xUnit tests |
| `docs/` | Architecture, roadmap, decisions |

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and [docs/ROADMAP.md](docs/ROADMAP.md).

## License

[GPL-3.0](LICENSE). Code ported from Sonarr, Radarr and Prowlarr (also GPL-3.0) is
listed in [NOTICE.md](NOTICE.md).
