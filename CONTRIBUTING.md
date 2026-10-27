# Contributing to StreamZH

Thanks for helping. Bug reports, ideas, documentation fixes and code are all welcome. By taking part you agree to the
[code of conduct](CODE_OF_CONDUCT.md).

- **Bugs and ideas**: open an [issue](https://github.com/streamzh/streamzh/issues) with the bug report or feature
  request form. Search first; maybe it is already there.
- **Security problems**: never in a public issue. See [SECURITY.md](SECURITY.md).
- **Bigger changes**: open an issue first and describe what you want to change, so we can agree on the approach before
  you spend time on it.

## Build from source

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download), [Node.js](https://nodejs.org/) 22 or newer,
Git. Optional for downloads during development: `par2` and `7z` on `PATH` (StreamZH installs them itself otherwise).

```bash
git clone https://github.com/streamzh/streamzh.git
cd streamzh

# Web interface (builds into src/StreamZH.Host/wwwroot)
cd frontend
npm ci
npm run build
cd ..

# Backend on http://localhost:9797, data in ./.devdata
dotnet run --project src/StreamZH.Host -- --data .devdata
```

Open http://localhost:9797, create the owner account, add a root folder (the owner allows it as a storage location in
one click) and your free TMDB API key in Settings → Metadata.

For work on the web interface run `npm run dev` in `frontend/` (http://localhost:5173). It proxies `/api` and `/hub`
to the backend on port 9797, with hot reload. To try a UI change against another StreamZH (e.g. a test server in
your network), set `STREAMZH_BACKEND` first, in PowerShell `$env:STREAMZH_BACKEND = "http://192.168.1.10:9797"`.

### Development switches

| Option | Use |
|---|---|
| `--data <folder>` | A data folder of its own, e.g. `.devdata` (ignored by Git) |
| `--urls http://localhost:7000` | Another port |
| `--tmdb-url http://localhost:9800/3/` | Point StreamZH at a TMDB stand-in (tests and offline work) |
| `--xem-url`, `--discord-url` | Stand-ins for TheXEM and the Discord API |
| `--auth-method forms` | Back to the sign-in page after locking yourself out |

### Tests and checks

```bash
dotnet build StreamZH.slnx              # warnings are errors
dotnet test tests/StreamZH.Tests        # xUnit, a few minutes
cd frontend && npx tsc -b && npx oxlint src && npm run build
```

Run a part of the tests with `--filter`, e.g. `dotnet test tests/StreamZH.Tests --filter "FullyQualifiedName~Parsing"`.

### Docker image and packages

```bash
docker build -f docker/Dockerfile -t streamzh:dev .
docker run --rm -p 9797:9797 -v "$PWD/.devdata-docker:/config" streamzh:dev
```

How the Windows installer, the Linux packages and releases are built is described in
[docs/PACKAGING.md](docs/PACKAGING.md).

On Windows, `tools\Update-Local.ps1` builds StreamZH from your clone and installs it locally (see the help at the top
of the script: `Get-Help .\tools\Update-Local.ps1 -Detailed`).

### CI

Every push and pull request builds and tests (GitHub Actions). Docker images and packages are built for version
tags and when started by hand.

## Repository layout

| Path | Content |
|---|---|
| `src/StreamZH.Host` | ASP.NET Core host: API, SignalR hub, authentication, scheduler; serves the web interface |
| `src/StreamZH.Core` | Domain model, decision engine, pipeline, event bus |
| `src/StreamZH.Data` | SQLite database, FluentMigrator migrations, repositories |
| `src/StreamZH.Parsing` | Release name, quality and language parser |
| `src/StreamZH.Metadata` | TMDB client |
| `src/StreamZH.Indexers` | Newznab and Torznab clients, search, RSS |
| `src/StreamZH.Usenet` | Usenet engine: NZB, NNTP, yEnc, par2, unpack |
| `src/StreamZH.Torrent` | Built-in torrent engine (MonoTorrent) |
| `src/StreamZH.Library` | Library, import, rename, downloads, notifications, requests, migration |
| `src/StreamZH.Tray`, `src/StreamZH.Tray.Core` | Windows tray icon and its platform-independent logic |
| `frontend/` | React + TypeScript web interface (Vite) |
| `tests/StreamZH.Tests` | xUnit tests |
| `docker/`, `tools/` | Container image, install and service scripts |
| `docs/` | User guide, installation, security, architecture, decisions, roadmap |

[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) explains how the parts fit together, [docs/DECISIONS.md](docs/DECISIONS.md)
why things are the way they are.

## Guidelines

- **English everywhere**: code, comments, UI texts, log messages, docs.
- **Small, focused pull requests** with tests for what you change. All tests must pass and the build has no warnings.
- **License header**: every C# file starts with the three-line GPL header used in the repository. Code ported from
  Radarr, Sonarr, Prowlarr or other projects keeps its origin: add the "Portions based on …" line (see
  `src/StreamZH.Library/Search/AutomaticSearchService.cs`) and list it in [NOTICE.md](NOTICE.md).
- **Settings**: simple by default; expert settings go behind the Advanced switch. Help texts are short and plain.
- **Security**: read [docs/SECURITY.md](docs/SECURITY.md) before you touch authentication, folders, secrets, tools or
  outgoing requests. Every request from the web interface sends `X-StreamZH-Request`; owner-only settings stay
  owner-only; folders must stay inside the storage locations.
- **Frontend style**: single quotes, no semicolons, two-space indentation, CSS modules with the design tokens. Do not
  reformat files you do not change.
- **No indexer lists, no copyrighted material**: StreamZH ships no indexers or content. No real posters or artwork in
  tests or screenshots, no copied third-party documentation.
- **No personal data** in code, tests, fixtures or screenshots: use example names (johndoe, John Doe), example
  addresses (192.168.1.10, example.com) and default ports.
- Record decisions that shape the project as a row in [docs/DECISIONS.md](docs/DECISIONS.md).

## License

StreamZH is licensed under the [GNU General Public License v3.0 only](LICENSE). Contributions are accepted under the
same license.
