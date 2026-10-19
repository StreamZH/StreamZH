<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/brand/streamzh-logo-dark.svg">
    <img src="docs/brand/streamzh-logo.svg" width="360" alt="StreamZH">
  </picture>
</p>

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
# Open http://localhost:9797, create the owner account, then add a root folder
# and your free TMDB API key under Settings

# Tests
dotnet test
```

Without a TMDB key you can point StreamZH at a TMDB stand-in for development:
`--tmdb-url http://localhost:9800/3/`.

For UI work run `npm run dev` in `frontend/` (http://localhost:5173). It proxies
`/api` and `/hub` to the backend on port 9797.

### CI

Every push builds and tests (about 3 minutes). The Docker image and the packages for
Windows, Linux and macOS are built only when started by hand (Actions → CI → Run workflow)
or for a version tag such as `v0.3.0`.

## Install

StreamZH runs unattended as a Windows service, a systemd service on Linux or a Docker
container. Your data stays in the data folder in all cases (`C:\ProgramData\StreamZH`,
`/var/lib/streamzh`, `/config`); updating only replaces the program files.

Coming from Sonarr, Radarr, Prowlarr and SABnzbd? [docs/MIGRATION.md](docs/MIGRATION.md) walks through taking
everything over, the side-by-side phase and switching over.

### Windows: build, install, run as a service

One command pulls the latest code, builds `StreamZH.exe`, stops the running instance,
installs it to `C:\Tools\StreamZH` and starts it again. Data in `C:\ProgramData\StreamZH` is kept.

```powershell
C:\Repos\StreamZH\tools\Update-Local.ps1
```

Needs Git, the .NET 10 SDK and Node.js LTS (`winget install --id Microsoft.DotNet.SDK.10 -e`,
`winget install --id OpenJS.NodeJS.LTS -e`).

Started like this, StreamZH runs in a console window and stops when you log off. To run it
as the Windows service "StreamZH" instead, run once in a PowerShell **as administrator**:

```powershell
C:\Repos\StreamZH\tools\Install-Service.ps1 -Firewall -AllowControlledFolderAccess
```

The service starts with Windows (delayed automatic start), keeps running without a login,
and Windows restarts it after a crash (after 5 s, 10 s, then every 60 s). Restart and
Shutdown in the web UI work as before; System → Status shows "Runs as: Windows service".
Running the script again updates the service. From then on `Update-Local.ps1` stops the
service, installs the new build, starts the service and waits until it answers; it needs no
administrator rights for that (the install script allows your user to start and stop the service).

| Option | Effect |
|---|---|
| `-Firewall` (`-Port 9797`) | Allows the port from private and domain networks |
| `-AllowControlledFolderAccess` | Adds `StreamZH.exe` to the allowed apps of Controlled folder access (Windows Security → Ransomware protection). Without it the service cannot import into protected folders such as Videos |
| `-Folder 'D:\Media', 'D:\Downloads'` | Lets the service account change these folders (root folders, download folders). Without it the script asks once when the service gets the virtual account |
| `-Account .\johndoe` | Runs the service as this user (asks for the password; Microsoft account: its password, not the PIN) instead of the virtual account. Needed for a NAS share that wants a user and password. `-Account LocalSystem` runs it as LocalSystem (not recommended) |
| `-DataDir D:\StreamZH` | Another data folder (passed as `--data`) |
| `-NoStart` | Creates or updates the service without starting it |
| `-NoTray` | No tray icon (removes its "Start with Windows" entry) |

The service runs as its own virtual account `NT SERVICE\StreamZH`: no password, and no rights on
the computer beyond its data folder and the folders it is given. It can change folders every user
may change (most folders on other drives); for folders of your user (Videos, Downloads, Desktop) or
other restricted folders, name them with `-Folder` or when the script asks. A service that ran as
LocalSystem (before 0.12) is switched to the virtual account when you run the script again, unless you
pass `-Account LocalSystem`. The data folder `C:\ProgramData\StreamZH` is private: only SYSTEM,
administrators, the service account and your user may open it (StreamZH checks this at every start).

Things that work differently for a service:

- Mapped network drives (`Z:`) do not exist for services. Use UNC paths (`\\nas\movies`)
  for root folders and download folders; the install script lists your mapped drives with their UNC paths.
- The virtual account logs on to other computers as the computer account (`DOMAIN\PC$`). Use
  `-Account .\<user>` when the NAS share needs a user and password.
- The proxy of your Windows user does not apply to the service; set a proxy in Settings → General if you need one.
- Logs stay in `C:\ProgramData\StreamZH\logs`. When StreamZH cannot start at all, the error is
  also in the Windows event log (Application, source StreamZH).

Back to the console window: `C:\Repos\StreamZH\tools\Uninstall-Service.ps1` (as administrator;
program and data stay).

#### Tray icon

A service has no window, so `StreamZH.Tray.exe` (next to `StreamZH.exe`) shows StreamZH in the
notification area, bottom right: a dot shows the state (green running, orange paused or warnings,
red stopped or not answering), the tooltip the downloads (`Running · 2 downloads · 47.3 % · 12 min 05 s`).
Double-click opens the dashboard; the right-click menu pauses and resumes downloads, restarts, stops
and starts StreamZH, opens the logs and the data folder, and switches "Start with Windows" and the
notifications when a download finishes or fails. `Update-Local.ps1` and `Install-Service.ps1` build it,
let it start with Windows for your user and start it (`-NoTray` leaves it out); it works for the
service and for the console window. It needs the .NET 10 Windows Desktop Runtime, which the .NET SDK
includes. The tray talks to StreamZH on this computer only, with a token StreamZH writes to
`C:\ProgramData\StreamZH\tray.token` at every start (readable only by the account StreamZH runs as and
the user the install script set up, or your own user for the console window).

### Linux: systemd service

Download a Linux package (Actions → CI → artifact `StreamZH-linux-x64` or `StreamZH-linux-arm64`),
unpack it and run the installer that comes with it:

```bash
unzip StreamZH-linux-x64.zip -d StreamZH && cd StreamZH
sudo bash install-linux.sh
```

It creates the system user `streamzh`, installs the program to `/opt/streamzh`, the data to
`/var/lib/streamzh`, writes `/etc/systemd/system/streamzh.service` (sample:
[docs/streamzh.service](docs/streamzh.service)), enables and starts it. Running it again with a
newer package updates StreamZH (`sudo bash install-linux.sh --from StreamZH-linux-x64.zip` also
takes a `.zip` or `.tar.gz` directly). `--uninstall` removes service and program, `--purge` also
the data. `bash install-linux.sh --help` lists all options.

The service user needs read and write access to your media and download folders, for example
`sudo setfacl -R -m u:streamzh:rwX -m d:u:streamzh:rwX /srv/media`. Logs: `journalctl -u streamzh -f`.
StreamZH needs ICU (`sudo apt install libicu-dev`) and installs par2 and 7-Zip itself (needs `tar` and `xz`).

### Docker

```bash
docker build -f docker/Dockerfile -t streamzh:dev .
docker run -d --name streamzh --restart unless-stopped -p 9797:9797 \
  -e PUID=1000 -e PGID=1000 -e UMASK=027 \
  -v /opt/streamzh:/config -v /srv/media:/media streamzh:dev
```

Like the linuxserver.io images, `PUID`/`PGID` set the user StreamZH runs as (default: the
image's `app` user, 1654) and `/config` is given to that user and closed to others; `--user`
works as well. `UMASK` (default `027`) decides who may read new media files: the group yes, others
no; `002` also lets the group change them, `022` lets everyone read them. The
image has a health check on `/health` (port 9797 inside the container; publish another port
with `-p`, do not change it in Settings).

## API

Interactive clients use the session cookie from the web UI. Scripts and CloudZH send the
API key (Settings → API key) as header:

```bash
curl -H "X-Api-Key: <key>" http://localhost:9797/api/v1/system/status
```

The OpenAPI description is at `/api/v1/openapi.json`. `/health` needs no login.

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
| `src/StreamZH.Torrent` | Built-in torrent engine (MonoTorrent) |
| `src/StreamZH.Library` | Import, rename, disk scan, media server notifications |
| `src/StreamZH.Tray`, `src/StreamZH.Tray.Core` | Windows tray icon (WinForms) and its logic without UI |
| `frontend/` | React + TypeScript web UI |
| `tests/` | xUnit tests |
| `docs/` | Architecture, roadmap, decisions |

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and [docs/ROADMAP.md](docs/ROADMAP.md).

## License

[GPL-3.0](LICENSE). Code ported from Sonarr, Radarr and Prowlarr (also GPL-3.0) is
listed in [NOTICE.md](NOTICE.md).
