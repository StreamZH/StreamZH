# Security

<!-- FX3 (0.12) sections below: data folder, service accounts, outgoing connections, downloaded files. The overall
     structure (threat model, authentication modes, reverse proxy, reporting vulnerabilities) comes from FX1; the
     main session merges both. -->

## The data folder is secret material

The data folder (`C:\ProgramData\StreamZH`, `~/.config/streamzh`, `/var/lib/streamzh` with the Linux installer,
`/config` in Docker) holds everything an attacker would want: the database with the API key, password hashes,
indexer API keys, download client and notification passwords, the sign-in keys (`keys/`) that sign the login
cookies, the backups (`Backups/`, which contain all of that again) and the logs.

StreamZH keeps the folder private and checks it at every start (it only ever takes rights away):

| System | What StreamZH enforces |
|---|---|
| Linux, macOS | A new data folder is `0700`. An existing one loses all rights for others and write for the group (the installer's `0750` stays). `streamzh.db` (and `-wal`, `-shm`), `tray.token`, everything in `keys/`, `Backups/` and `restore/` become `0600` (folders `0700`); logs at most `0640`. New backups are written `0600`, also in a backup folder outside the data folder. |
| Docker | As Linux. The entrypoint also closes `/config` to others (`chmod u+rwx,g-w,o-rwx`) and the default `UMASK` is `027`. |
| Windows | The folder gets a protected access list: SYSTEM, Administrators, the account StreamZH runs as and user accounts granted on purpose (the install script grants your user). Users, Authenticated Users, Everyone and Interactive lose access, and nothing is inherited from `C:\ProgramData`. The sign-in keys are additionally encrypted with DPAPI (machine scope for the service, user scope in a console window); keys written by older versions stay readable and are replaced by encrypted ones over time. |

When the account StreamZH runs as may not change the access list (for example the service account on a folder an
administrator created), the log says so at every start, with the fix: run `tools\Install-Service.ps1` (service) or
`tools\Uninstall-Service.ps1` (console window) once as administrator.

Treat backups like the data folder: whoever has a backup can read every stored password and sign in as any user
until the sign-in keys change. Keep backup folders on storage only you can read, and do not attach backups to bug
reports.

## Service accounts

Run StreamZH with the least rights that work:

- **Windows:** `tools\Install-Service.ps1` runs the service as its own virtual account `NT SERVICE\StreamZH` (no
  password, no rights beyond its data folder and the folders it is given). Give it your root folders and download
  folders with `-Folder 'D:\Media', 'D:\Downloads'` (or answer the prompt). A NAS share that wants a user and
  password needs `-Account .\<user>`; the virtual account logs on to other computers as the computer account.
  `-Account LocalSystem` is possible but not recommended: anyone who gets admin access to StreamZH (or its tool
  paths) would control all of Windows.
- **Linux:** `install-linux.sh` creates the system user `streamzh` without a shell; give it access to your media with
  groups or ACLs (`setfacl`), never run StreamZH as root.
- **Docker:** set `PUID`/`PGID` to an ordinary user (never `0`).

The tray icon (Windows) talks to StreamZH with a token in `<data>\tray.token` that only SYSTEM, the account
StreamZH runs as and the tray's user may read (the user the install script set up, or your own user when StreamZH
runs in a console window). On Linux and macOS the file is `0600`.

## Outgoing connections

- **Indexer links:** download links in Newznab/Torznab feeds and the redirects of an indexer may not lead to this
  computer (loopback), link-local addresses (`169.254.0.0/16`, `fe80::/10`, including `169.254.169.254`), unspecified
  or multicast addresses, or known cloud metadata addresses, unless they stay on the indexer's own host (Prowlarr or
  Jackett on the same machine keep working). Addresses on your LAN are allowed. The check runs on the resolved IP
  addresses before the request and again when the connection opens, so a DNS answer that changes in between (DNS
  rebinding) does not get through. When a proxy is configured, the proxy resolves the name; StreamZH checks its own
  resolution before sending.
- **Size limits:** answers from servers an admin configures are limited: import lists, connections and the
  Prowlarr import 20 MB, the Radarr/Sonarr migration 256 MB, the SABnzbd migration 64 MB, the TRaSH Guides archive
  256 MB (16 MB per file inside). NZB files are limited to 50 MB, .torrent files and indexer API answers as before.
- **Certificate validation** (Settings → General → Security, advanced): keep it **Enabled**. For your own servers
  with self-signed certificates choose **Disabled for local addresses**; **Disabled** for all sites lets anyone on
  the way to an indexer read its API key and is shown with a warning. The update check always validates GitHub's
  certificate and accepts only `https://github.com` release links, whatever this setting says.

## Downloaded files

- **Archives:** after unpacking, StreamZH deletes every symbolic link and junction the archive created and never
  follows links when it moves files. It prefers its own pinned 7-Zip; when the 7-Zip on `PATH` is older than 22
  (for example p7zip 16.02 on older NAS systems), it installs the pinned one, which is then used.
- **Media info:** ffprobe may only open local files (`-protocol_whitelist file,pipe`), so a downloaded "video" that
  is really a playlist cannot make it fetch URLs.
- **Torrent file names** may not point outside the download folder (absolute paths, `..`); such torrents are refused.
