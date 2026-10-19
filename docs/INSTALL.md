# Installing StreamZH

StreamZH is one program with a web interface. It runs as a Windows service, a systemd service on Linux, a Docker
container or a program on macOS, and keeps everything it knows in one **data folder**. Updating replaces the program
files only; the data folder stays.

- [Before you start](#before-you-start)
- [Windows](#windows)
- [Linux](#linux)
- [Docker](#docker)
- [macOS](#macos)
- [First start](#first-start)
- [Reverse proxy and HTTPS](#reverse-proxy-and-https)
- [Updating](#updating)
- [Uninstalling](#uninstalling)
- [Backups and restore](#backups-and-restore)
- [Moving the data folder](#moving-the-data-folder)
- [Command line and environment](#command-line-and-environment)

Downloads: the [releases page](https://github.com/streamzh/streamzh/releases) on GitHub. Every release lists a
`SHA256SUMS` file to check the downloads against.

## Before you start

| | |
|---|---|
| Systems | Windows 10 (1809 or later), Windows 11 or Windows Server 2019 or later (x64), Linux with systemd (x64, arm64), Docker (amd64, arm64), macOS (Apple silicon and Intel) |
| Ports | **9797** (web interface and API, TCP), **51414** TCP and UDP (built-in torrent engine, only when you use it) |
| Data folder | `C:\ProgramData\StreamZH` (Windows), `/var/lib/streamzh` (Linux packages), `/config` (Docker), `~/Library/Application Support/StreamZH` (macOS) |
| Tools | par2 (repair) and 7-Zip (unpack) are installed by StreamZH itself at the first start when they are missing. ffprobe (media info) too on Windows; on Linux and macOS install `ffmpeg` with your package manager. The Docker image has all three |
| Accounts you need | A free [TMDB](https://www.themoviedb.org/settings/api) API key; your indexers; a Usenet provider or a torrent setup |

The native packages are self-contained: no .NET runtime needs to be installed.

### Folders and permissions

StreamZH runs under its own account (a Windows service account, the Linux user `streamzh`, the Docker `PUID`). That
account needs read and write access to your **root folders** (where movies and series live) and your **download
folders**. Two things decide what StreamZH may touch:

1. **The operating system**: the folder permissions of StreamZH's account (set up below per platform).
2. **Storage locations** (Settings → General → Storage, owner only): the base folders StreamZH may use at all. A new
   installation allows only its own download folder; when you add your first root folder outside it, StreamZH offers
   to allow it in one click ("Allow and add"). See [docs/SECURITY.md](SECURITY.md#storage-locations).

Downloads and the library should be on the **same drive** when you can: imports are then instant moves (Usenet) or
hard links (torrents, which keep seeding) instead of copies.

## Windows

### Installer

Run `StreamZH-<version>-win-x64-setup.exe` (needs administrator rights) while signed in with the Windows account that
will use StreamZH: the tray icon and its "Start with Windows" entry are set up for the account that runs the setup.
StreamZH goes to `C:\Program Files\StreamZH`, your data to `C:\ProgramData\StreamZH`.

| Page | What you choose |
|---|---|
| **How should StreamZH run?** | **As a Windows service** (recommended, preselected): runs in the background, starts with Windows, also when nobody is signed in, and restarts after a crash. **Only while I am signed in**: the tray icon starts StreamZH when you sign in; it stops when you sign out. Options, all off by default: **Let other devices on my network open StreamZH** (Windows Firewall rule for TCP 9797, private and domain networks only), **Allow StreamZH in Controlled folder access** (only needed when Windows Security → Ransomware protection is on), **Add a desktop shortcut**. |
| **Where are your movies and series?** (service only) | The service runs as its own account and may change only folders it is allowed to use. **On another drive or a network share** (preselected): nothing to set up, folders on other drives are usually open to every user and a NAS decides itself who may write (use `\\server\share` paths in StreamZH). **In my Videos and Downloads folders**: the service may use these two folders of your user. **In other folders on this PC**: up to three folders of your choice. See [The service account](#the-service-account). |
| **Finish** | **Start StreamZH** and **Open StreamZH in my browser** (both ticked): starts the service and the tray icon (or the tray icon, which starts StreamZH) and opens `http://localhost:9797` as soon as StreamZH answers. |

The Start menu gets **StreamZH** (opens the web interface) and **StreamZH tray icon**. The setup has no license page:
StreamZH is free software under the GNU GPL v3, which needs no acceptance; the license is in the program folder and
linked on the first page.

**Updating:** run the setup of the new version. It closes StreamZH and the tray icon by itself, keeps your choices
(tick "Change how StreamZH runs" on the first page to change them), replaces the program and starts StreamZH again.
Your data and settings stay. If you installed StreamZH by hand before (a service running from another folder such as
`C:\Tools\StreamZH`), the setup moves the service to `C:\Program Files\StreamZH` and tells you that the old folder can
be deleted.

The firewall option opens only the web interface. When you use the built-in torrent engine and want incoming peers,
allow its port yourself (PowerShell as administrator):

```powershell
New-NetFirewallRule -DisplayName 'StreamZH torrent (TCP)' -Direction Inbound -Protocol TCP -LocalPort 51414 -Action Allow -Profile Private, Domain
New-NetFirewallRule -DisplayName 'StreamZH torrent (UDP)' -Direction Inbound -Protocol UDP -LocalPort 51414 -Action Allow -Profile Private, Domain
```

**Silent installation** (for scripts and deployment tools). `/TASKS` lists the choices: `service` or `trayonly`, plus
`firewall`, `cfa` (Controlled folder access) and `desktopicon`; what is not listed is off. `/FOLDERS` gives the service
account its folders, separated by `;`. A silent installation starts StreamZH at the end (not the browser):

```powershell
StreamZH-<version>-win-x64-setup.exe /VERYSILENT /TASKS="service,firewall,cfa" /FOLDERS="D:\Media;D:\Downloads"
StreamZH-<version>-win-x64-setup.exe /VERYSILENT /TASKS="trayonly"
```

The setup writes a log to `%TEMP%\Setup Log <date> #<n>.txt`, including the output of the service script.

A few things the setup refuses, on purpose:

- **Another program folder.** StreamZH installs inside `C:\Program Files` so that only administrators can change the
  program the service runs. To install elsewhere anyway, start the setup with `/ALLOWCUSTOMDIR` (for example
  `/ALLOWCUSTOMDIR /DIR="D:\Apps\StreamZH"`): the setup then creates the folder itself, owned by Administrators and
  closed to other users, before it copies any file. An existing folder that already holds files is refused. Choose a
  parent folder that only administrators can change (not one a standard user created, such as a `D:\Apps` of your
  user): whoever controls the parent can rename or replace the StreamZH folder.
- **"Tray only" from a deployment tool.** When the setup runs as SYSTEM (Intune, Configuration Manager, PsExec `-s`),
  "tray only" would start StreamZH as SYSTEM; the setup stops with a message. Use `/TASKS="service"` there.
- **A data folder someone else prepared.** If `C:\ProgramData\StreamZH` exists before the first installation and is a
  link, or belongs to another user and already holds files, the setup stops (the message shows how to take it over
  with `takeown` after you checked its contents). An empty folder of another user is replaced. On every installation the
  data folder gets Administrators as owner and exactly the access list StreamZH needs.

### Service or tray icon

A **service** has no window. The **tray icon** (`StreamZH.Tray.exe`, bottom right in the notification area) shows its
state: a green dot while it runs, orange while downloads are paused or there are warnings, red when it is stopped
or does not answer. The tooltip shows the downloads (`Running · 2 downloads · 47.3 % · 12 min 05 s`). Double-click
opens the dashboard; the menu pauses and resumes downloads, restarts, stops and starts StreamZH, opens the logs and
the data folder, shows the setup code before the first account exists, and switches "Start with Windows" and the
notifications for finished or failed downloads.

With **tray icon only**, StreamZH runs as a normal program of your user: it stops when you sign out, and it uses
your user's network drives and proxy. Choose the service for a server that runs unattended.

### The service account

The service runs as its own **virtual account** `NT SERVICE\StreamZH`: no password, and no rights beyond its data
folder and the folders it is given.

- It can change folders that every user may change (most folders on other drives, e.g. `D:\Media`).
- Folders of your user (Videos, Downloads, Desktop) and other restricted folders must be given to it: on the
  installer's page "Where are your movies and series?", or later with the service script (below).
- **Network shares:** a service does not see mapped drives (`Z:`). Use UNC paths such as `\\nas\media\movies` for
  root folders and download folders. The virtual account logs on to other computers as the computer account
  (`DOMAIN\PC$`); when your NAS wants a user name and password, run the service as a user account instead
  (`-Account .\johndoe`, below).
- The proxy of your Windows user does not apply to the service; set one in Settings → General → Proxy if needed.
- Logs are in `C:\ProgramData\StreamZH\logs`. When StreamZH cannot start at all, the error is also in the Windows
  event log (Application, source StreamZH).

### Service script

The installer sets up the service with `Install-Service.ps1`, which is installed with StreamZH in
`C:\Program Files\StreamZH\tools`. You can run it yourself in a PowerShell **as administrator** to change the service
later, for example to give it more folders:

```powershell
& 'C:\Program Files\StreamZH\tools\Install-Service.ps1' -Folder 'D:\Media', 'D:\Downloads'
```

| Option | Effect |
|---|---|
| `-InstallDir <folder>` | The folder with `StreamZH.exe` (default: the folder above `tools`, here `C:\Program Files\StreamZH`) |
| `-Folder 'D:\Media', 'D:\Downloads'` | Gives the service account "Modify" on these folders. Without it the script asks when the service switches to the virtual account. Network shares are not changed: the NAS decides who may write there |
| `-Account .\johndoe` | Runs the service as this user (asks for the password; for a Microsoft account its password, not the PIN). Needed for a NAS share that wants a user and password. `-Account LocalSystem` is possible but not recommended |
| `-Firewall` (`-Port 9797`) | Allows the port from private and domain networks |
| `-AllowControlledFolderAccess` | Adds `StreamZH.exe` to the allowed apps of Controlled folder access |
| `-DataDir D:\StreamZH` | Another data folder |
| `-NoStart` | Creates or updates the service without starting it |
| `-NoTray` | No tray icon for your user |

`Install-Tray.ps1` (as administrator) switches to **tray icon only**: it removes the service, registers the tray icon
to start with Windows for your user and starts it (`-NoStart` leaves that out; `-Firewall` and
`-AllowControlledFolderAccess` work as above). `Install-Service.ps1` switches back. `Uninstall-Service.ps1` (as
administrator) removes the service, its firewall rule and the tray icon's autostart; program and data stay.

### Portable zip

`StreamZH-<version>-win-x64.zip` holds the same program without an installer, in a folder `StreamZH` with the scripts
in `StreamZH\tools`. Move that folder to a place that only administrators may change (for example
`C:\Program Files\StreamZH`; StreamZH warns "Program folder not protected" otherwise) and start `StreamZH.exe`: it
runs in a console window and uses `C:\ProgramData\StreamZH` as well. To run it as a service or with the tray icon,
run `tools\Install-Service.ps1` or `tools\Install-Tray.ps1` from that folder as administrator.

## Linux

The packages install the program to `/opt/streamzh`, create the system user `streamzh` (no shell), keep the data in
`/var/lib/streamzh` and run StreamZH as the systemd service `streamzh.service`, enabled and started.

### Debian, Ubuntu (.deb)

```bash
sudo apt install ./streamzh_<version>_amd64.deb
```

### Fedora, RHEL, openSUSE (.rpm)

```bash
sudo dnf install ./streamzh-<version>.x86_64.rpm     # openSUSE: sudo zypper install ./streamzh-<version>.x86_64.rpm
```

On 64-bit ARM (for example a Raspberry Pi 4 or 5 with a 64-bit system) use `streamzh_<version>_arm64.deb` or
`streamzh-<version>.aarch64.rpm`.

On a new installation the package prints how to reach StreamZH and where the setup code is. The setup code is also
in the file `/var/lib/streamzh/setup-code` and in the journal: `sudo journalctl -u streamzh | grep -i 'setup code'`.

### Any distribution with systemd (.tar.gz and install script)

`StreamZH-<version>-linux-x64.tar.gz` (and `StreamZH-<version>-linux-arm64.tar.gz`) contains one folder of the same
name with the program, `install-linux.sh`, `LICENSE` and `NOTICE.md`:

```bash
tar -xzf StreamZH-<version>-linux-x64.tar.gz
sudo bash StreamZH-<version>-linux-x64/install-linux.sh
```

The script does what the packages do and writes `/etc/systemd/system/streamzh.service` (a sample is in
[streamzh.service](streamzh.service)). Running it again with a newer package updates StreamZH.

| Option | Effect |
|---|---|
| `--from <path>` | Unpacked package folder, `.tar.gz` or `.zip` (default: the folder of the script) |
| `--prefix <dir>` | Program folder (default `/opt/streamzh`) |
| `--data <dir>` | Data folder (default `/var/lib/streamzh`) |
| `--user <name>` | Service user, created when missing (default `streamzh`) |
| `--port <port>` | Port to check after the start (default 9797; the port itself is set in Settings → General) |
| `--no-start` | Install, but do not start or restart the service |
| `--uninstall` | Stop and remove the service and the program folder; data and user stay |
| `--purge` | With `--uninstall`: also delete the data folder and the user |

### Permissions

The user `streamzh` needs read and write access to your media and download folders. Either with an access control
list (the second `-m` makes new subfolders inherit it):

```bash
sudo setfacl -R -m u:streamzh:rwX -m d:u:streamzh:rwX /data/media /data/downloads
```

or by adding it to the group that owns them: `sudo usermod -aG media streamzh` (then
`sudo systemctl restart streamzh`). The service writes new files with umask `0002` (group may read and write), so
Plex or Jellyfin in the same group can read imported media. Never run StreamZH as root.

### ICU

.NET needs the ICU library. Most desktop and server distributions have it; minimal images may not:

```bash
sudo apt install libicu-dev     # Debian, Ubuntu (or the versioned libicuNN package)
sudo dnf install libicu         # Fedora, RHEL
```

### Service commands

```bash
systemctl status streamzh
sudo systemctl restart streamzh
journalctl -u streamzh -f           # log; the log files are in /var/lib/streamzh/logs
sudo systemctl edit streamzh        # your own changes to the unit (kept on updates)
```

## Docker

The image `ghcr.io/streamzh/streamzh` is built for amd64 and arm64 and contains par2, 7-Zip and ffprobe. Tags: the
version (`1.0.0`), the minor version (`1.0`) and `latest` for the newest release. Release candidates are only
available by their version tag, e.g. `ghcr.io/streamzh/streamzh:1.0.0-rc.1`; they never move `1.0` or `latest`.

### docker run

```bash
docker run -d --name streamzh --restart unless-stopped \
  -p 9797:9797 -p 51414:51414 -p 51414:51414/udp \
  -e PUID=1000 -e PGID=1000 -e UMASK=027 -e TZ=Etc/UTC \
  -v /opt/streamzh/config:/config \
  -v /data/media:/data/media -v /data/downloads:/data/downloads \
  ghcr.io/streamzh/streamzh:latest
```

### Docker Compose

```yaml
services:
  streamzh:
    image: ghcr.io/streamzh/streamzh:latest
    container_name: streamzh
    restart: unless-stopped
    environment:
      PUID: "1000"
      PGID: "1000"
      UMASK: "027"
      TZ: "Etc/UTC"
    ports:
      - "9797:9797"            # web interface; "127.0.0.1:9797:9797" behind a reverse proxy on this host
      - "51414:51414"          # built-in torrent engine (only when you use it)
      - "51414:51414/udp"
    volumes:
      - ./config:/config
      - /data/media:/data/media
      - /data/downloads:/data/downloads
    security_opt:
      - no-new-privileges:true
```

The repository has a commented example that builds the image from source:
[docker/docker-compose.example.yml](../docker/docker-compose.example.yml).

| Setting | Meaning |
|---|---|
| `PUID`, `PGID` | The user and group StreamZH runs as, like the linuxserver.io images: use the owner of your media (`id -u`, `id -g`). The container starts as root only to give `/config` to this user, then drops to it. Never `0`. Default: the image's `app` user (1654). `--user 1000:1000` works as well |
| `UMASK` | Who may read new files. `027` (default): the group yes, others no. `002`: the group may change them too. `022`: everyone may read them |
| `TZ` | Time zone for the logs and the scheduler |
| `/config` | Database, settings, sign-in keys, logs, backups. Back it up; it is closed to other users |
| `9797` | Web interface and API inside the container. Publish another host port with `-p 8080:9797`; do not change 9797 in Settings |
| `51414` TCP and UDP | Listening port of the built-in torrent engine (Settings → Download clients). If you change it there, publish the new port |

**Paths:** mount media and downloads under one parent folder (for example `/data`) so imports can move files and
create hard links instead of copying. If you migrate from Radarr, Sonarr or SABnzbd in Docker, mount the folders at
the **same paths** they use; then the migration wizard needs no path mappings. After the first start, allow the
container paths (`/data/media`, `/data/downloads`) as storage locations, or let the root-folder dialog do it.

The image has a health check on `/health`; `docker ps` shows `healthy`. With Docker your browser always counts as
"another device", so the setup page asks for the setup code: `docker logs streamzh` shows it.

## macOS

`StreamZH-<version>-osx-arm64.zip` is for Macs with Apple silicon, `StreamZH-<version>-osx-x64.zip` for Macs with an
Intel processor. Each contains a folder `StreamZH` with the program. The app is not notarized by Apple, so macOS
blocks it after a download until you remove the quarantine flag:

```bash
cd ~/Downloads
unzip StreamZH-<version>-osx-arm64.zip
xattr -dr com.apple.quarantine StreamZH
./StreamZH/StreamZH
```

StreamZH runs in the Terminal window and keeps its data in `~/Library/Application Support/StreamZH`. Install
`ffmpeg` (for example with Homebrew) for media info; par2 and 7-Zip are installed by StreamZH. To start it
automatically, add it as a login item or run it from a `launchd` agent of your own.

## First start

1. Open `http://localhost:9797` on the server, or `http://<server>:9797` from another device.
2. Create the **owner** account. On the server itself no code is needed. From any other device (and always with
   Docker) the setup page asks for the **setup code**, which StreamZH shows
   - in its console and log (`docker logs streamzh`, `sudo journalctl -u streamzh | grep -i 'setup code'`),
   - in the file `setup-code` in the data folder (`/var/lib/streamzh/setup-code` with the Linux packages,
     `C:\ProgramData\StreamZH\setup-code` on Windows; readable by StreamZH's account and administrators only),
   - under **Show setup code** in the Windows tray icon.

   The code is no longer needed once the owner account exists.
3. Follow the [getting started guide](guide/getting-started.md).

If port 9797 is taken by another program, StreamZH cannot start; see
[Troubleshooting](guide/troubleshooting.md#port-already-in-use).

## Reverse proxy and HTTPS

To use StreamZH from outside your home, put it behind a reverse proxy with HTTPS and do **not** forward port 9797 on
your router. In StreamZH (Settings → General, owner only; switch on Advanced for the security settings):

| Setting | Value |
|---|---|
| Bind address | `127.0.0.1` when the proxy runs on the same computer (Docker: publish `127.0.0.1:9797:9797` instead) |
| URL base | `/streamzh` when the proxy serves StreamZH below a path, empty for its own host name |
| Application URL | The public address including the URL base, e.g. `https://media.example.com/streamzh` |
| Trusted proxies | The address of the proxy as StreamZH sees it, e.g. `127.0.0.1` or `172.18.0.2` |
| Client IP header | **Auto** works for the examples below |
| Authentication | Keep **Forms**. External only together with trusted proxies (see [docs/SECURITY.md](SECURITY.md#authentication-modes-and-their-risks)) |

Host settings apply after a restart (StreamZH shows "Restart required"). The proxy must pass the path on unchanged
(no prefix stripping), send `X-Forwarded-For` and `X-Forwarded-Proto`, and allow WebSockets for `/hub` (live updates).

### nginx

```nginx
location /streamzh {
    proxy_pass http://127.0.0.1:9797;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection $http_connection;
    client_max_body_size 100m;   # backup restores and NZB uploads
}
```

### Caddy

```caddyfile
media.example.com {
    handle /streamzh* {
        reverse_proxy 127.0.0.1:9797
    }
}
```

Caddy sends `X-Forwarded-For` and `X-Forwarded-Proto` and handles WebSockets by itself.

### Traefik (Docker labels)

```yaml
services:
  streamzh:
    image: ghcr.io/streamzh/streamzh:latest
    labels:
      - traefik.enable=true
      - traefik.http.routers.streamzh.rule=Host(`media.example.com`) && PathPrefix(`/streamzh`)
      - traefik.http.routers.streamzh.entrypoints=websecure
      - traefik.http.routers.streamzh.tls.certresolver=letsencrypt
      - traefik.http.services.streamzh.loadbalancer.server.port=9797
```

Do not add a StripPrefix middleware: StreamZH serves the URL base itself. Without a published port, set **Trusted
proxies** to the Docker network of Traefik (for example `172.18.0.0/16`).

### HTTPS in StreamZH itself

Without a proxy, StreamZH can serve HTTPS with your own certificate (Settings → General → Host, Advanced: HTTPS,
HTTPS port 9798, a `.pfx` file and its password). HTTP stays on port 9797.

## Updating

Settings → General → Updates → **Check for updates** asks GitHub for the latest release. When there is a newer one,
it shows a **Download** link for the file that fits this installation (the setup for an installer installation, the
`.deb` or `.rpm` for a package, otherwise the `.zip` or `.tar.gz` for this platform and processor; Docker: pull the
image) and how to update. StreamZH does not install updates by itself. Make a backup first (System → Backup →
Backup now), then:

| Installed with | Update |
|---|---|
| Windows installer | Run the new `StreamZH-<version>-win-x64-setup.exe`. It closes StreamZH, replaces the program and starts it again |
| Windows portable zip | Shut StreamZH down (System → Status → Shut down), replace the files, start it again |
| .deb | `sudo apt install ./streamzh_<new version>_amd64.deb` |
| .rpm | `sudo dnf install ./streamzh-<new version>.x86_64.rpm` |
| tar.gz | `sudo bash /opt/streamzh/install-linux.sh --from StreamZH-<new version>-linux-x64.tar.gz` (or unpack the new file and run its `install-linux.sh`) |
| Docker | `docker pull ghcr.io/streamzh/streamzh:latest`, then recreate the container (`docker compose pull && docker compose up -d`) |
| macOS | Stop StreamZH, replace the `StreamZH` folder, remove the quarantine flag again, start it |

The database is upgraded at the first start of the new version. Going back to an older version needs the backup you
made before.

## Uninstalling

| Installed with | Uninstall | Your data |
|---|---|---|
| Windows installer | Settings → Apps → StreamZH → Uninstall | Kept in `C:\ProgramData\StreamZH` unless you tick "Also delete my data" |
| .deb | `sudo apt remove streamzh` | Data and the user `streamzh` are kept; `sudo apt purge streamzh` deletes both |
| .rpm | `sudo dnf remove streamzh` | Data and user are kept (rpm has no purge); to delete them: `sudo rm -rf /var/lib/streamzh && sudo userdel streamzh` |
| tar.gz | `sudo bash /opt/streamzh/install-linux.sh --uninstall` | Kept; add `--purge` to delete data and user |
| Docker | `docker rm -f streamzh` | Kept in your `/config` folder |
| macOS | Delete the `StreamZH` folder | Kept in `~/Library/Application Support/StreamZH` |

The Windows uninstaller asks one question: **Remove StreamZH**, with the box **Also delete my data** (off by default;
it deletes `C:\ProgramData\StreamZH` with the library, settings and backups). It closes StreamZH and the tray icon by
itself and removes the service, its firewall rule and the tray icon. Silently:
`"C:\Program Files\StreamZH\unins000.exe" /VERYSILENT` keeps the data, `/VERYSILENT /DELETEDATA=1` deletes it too.

Your media and download folders are never deleted by an uninstall.

## Backups and restore

System → Backup (owner only). A backup is a zip with the database (settings, library, history, users) and the
sign-in keys. Your media files are not part of it.

- **Scheduled backups** every 7 days, kept for 28 days (Settings → General → Backups: folder, interval, retention).
  Another drive is safer than the default `<data>/Backups`.
- **Backup now** makes a manual backup; manual backups are not deleted by the retention.
- **Restore**: pick a backup from the list or **Restore from a file**. StreamZH checks it and stages it; it is applied
  at the next start, before the database opens ("Restore at restart", then restart). The replaced database stays in
  `<data>/restore/` in case you need it.

A backup contains every stored password and API key and the keys that sign the login cookies: keep it as private as
the data folder and never attach it to a bug report. On Windows the keys are additionally encrypted with Windows'
own data protection, for this computer (service) or for your Windows user (tray icon only, console). Restored on
another computer, or under another user, the keys cannot be read: you sign in again, and the status window lists
every stored password and API key as "secret unreadable" with a link to enter it again.

## Moving the data folder

1. Make a backup, then stop StreamZH.
2. Copy the whole data folder to the new place, including `keys/` (without it, stored passwords and API keys must be
   entered again). Keep it private: only StreamZH's account (and administrators) may read it.
3. Point StreamZH at it:
   - Windows service: `Install-Service.ps1 -InstallDir 'C:\Program Files\StreamZH' -DataDir 'D:\StreamZH'` (as administrator).
   - Linux: `sudo systemctl edit streamzh` and add `[Service]` `Environment=STREAMZH_DATA=/srv/streamzh`, or run
     `install-linux.sh --data /srv/streamzh`. The folder must belong to the user `streamzh`.
   - Docker: mount the new folder at `/config`.
   - Elsewhere: start StreamZH with `--data <folder>` or set `STREAMZH_DATA`.
4. Start StreamZH and check System → Status → Data folder.

The default download folder (`<data>/downloads`) moves with the data folder. If you used it, check the storage
locations (Settings → General → Storage) and the download folders (Settings → Downloads) afterwards.

## Command line and environment

| Option | Environment | Effect |
|---|---|---|
| `--data <folder>` | `STREAMZH_DATA` | Data folder |
| `--urls http://*:8080` | `ASPNETCORE_URLS` | Listen on these addresses instead of the Host settings (the settings page then shows the override) |
| `--auth-method forms` | | Brings the sign-in page back when you locked yourself out with another authentication method |

The data folder is found in this order: `--data`, `STREAMZH_DATA`, `/config` in a container, then the system default.
StreamZH also honours `HTTP_PROXY` and `HTTPS_PROXY` when no proxy is set in Settings → General.
