# Security

<!-- FX2 (0.12) sections: roles, owner-only settings, stored secrets, secrets in links, backups, tools StreamZH runs. -->

## Roles, the API key and owner-only settings

StreamZH has four roles: **Owner**, **Admin**, **Friend** and **Requester**. Admins configure almost everything, but
some things would let an Admin take over the owner's account or run programs on the host. Since 0.12 only the owner may:

| Owner only | Why |
|---|---|
| See the API key, create a new one | The key is a login that never expires |
| Security settings: authentication method, "Authentication required", certificate validation (and the reverse-proxy trust settings) | They decide who gets in and whether StreamZH trusts certificates |
| Outgoing proxy | The proxy sees every web request, with indexer keys and passwords |
| Backups: create, list, download, restore, delete, and the backup folder, interval and retention | A backup holds every secret and the key ring; a restore replaces the accounts |
| Tool paths (par2, 7-Zip, ffprobe) and the extra par2/7-Zip parameters | StreamZH runs these programs with its own rights |
| Managing other owners | Unchanged since 0.10 |

An Admin can still send these settings **unchanged** (the web app saves whole sections); a change is refused with
"Only the owner can change this." The web app shows these sections read-only for Admins.

**The API key acts as an Admin** (since 0.12; before, it acted as the owner). Scripts, CloudZH and other tools keep
working: they can do everything an Admin can (library, downloads, indexers, settings, requests). For the owner-only
things above, sign in as the owner. A leaked key therefore cannot read itself back, change the sign-in, restore a
backup or point a tool path at another program.

The **local network bypass** ("Authentication required: Disabled for local addresses") and **External** authentication
stay the owner: they are the owner's own way in (there is no account to take a role from), and turning them on is
itself owner-only. Use them only where everyone on that network or behind that proxy may be the owner.

## Stored secrets

Indexer API keys, Usenet passwords, download-client passwords, the secrets of Connect and import lists (passwords,
tokens, API keys, webhook URLs; Plex and Trakt tokens included), the Discord bot token, the TMDB token, the proxy
password and StreamZH's own API key are stored **encrypted** in the database (`enc:v1:` followed by a payload of ASP.NET
Data Protection, purpose `StreamZH.Secrets.v1`). The keys are in `<data>/keys`, the same key ring that signs the login
cookies. Values from before 0.12 (and from restored old backups) are encrypted at the next start.

- The database alone (a copy of `streamzh.db`) does not reveal these secrets; the database **and** the key folder do.
- The SSL certificate password stays readable in the database: StreamZH needs it before the key ring is loaded.
  Protect the certificate file and the data folder instead.
- RAR passwords of downloads (from the NZB) and the password hashes of accounts are not encrypted this way (the hashes
  are PBKDF2 and cannot be reversed).
- If the key folder is lost or the database is moved without it, nothing breaks: each affected secret reads as
  "not set", the status window says "&lt;item&gt;: secret unreadable" with a link, and the item works again once the
  secret is entered again. A lost API key is not replaced automatically; create a new one in Settings → General.

The API never returns a stored secret: it shows `********`. Sending `********` back keeps the stored secret **only
while the destination is unchanged** (URL, host, port, SSL, URL base, API path, server). If you change the address,
type the secret again ("Enter the password again to test a changed address."). This applies to saving and to every
Test button (Connect, import lists, Usenet servers, download clients, indexers including "Clone"), so nobody who can
edit settings can send a stored secret to an address of their choice.

## Secrets in links

Download links of releases carry the indexer's key (`apikey=`, `r=`, `jackett_apikey=`, `passkey=`, `token=`, …).
StreamZH keeps them only in memory for the grab. The history, release guids, info links and stored error messages
keep the link without these values (`(removed)`), older history entries are cleaned at the update to 0.12, and
Friends never see the download link at all.

## Backups are secret material

A backup zip contains the database **and** the key ring, so whoever has a backup can read every stored secret and
sign in as any user. Only the owner can make, download or restore backups. Keep the backup folder where only the
owner and StreamZH can read it, and treat backup files like passwords.

## Tools StreamZH runs

StreamZH runs par2, 7-Zip and ffprobe. A configured path is only used when the file is named like the tool
(`par2`, `par2.exe`, `par2j*.exe`; `7z`, `7za`, `7zz`, `7z.exe`, `7za.exe`; `ffprobe`, `ffprobe.exe`); anything else is
refused, also when it was stored by an earlier version. Extra parameters are limited to an allow-list
(par2: `-t`, `-T`, `-m`, `-N`, `-S`, `-q`, `-v`; 7-Zip: `-mmt`, `-bb`, `-bd`, `-bt`, `-bs*`, `-scc*`, `-mcp=`, `-ssc`,
`-ao*`); output folder, password and file selection are always set by StreamZH. Arguments go to the program as a
list, never through a shell.
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
