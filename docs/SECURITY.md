# Security

StreamZH controls your media library, your indexer and Usenet accounts and the computer it runs on (it starts tools
such as par2 and 7-Zip and writes into your media folders). This page says what StreamZH protects, what it relies on
you for, and how to run it safely. Short version:

- Keep sign-in on (the default). Use "Disabled for local addresses" only on a home network you trust.
- Reach StreamZH from the internet only through a reverse proxy with HTTPS, never by forwarding port 9797.
- Treat the API key, backups and the data folder like passwords (see [The data folder is secret material](#the-data-folder-is-secret-material)).
- Run StreamZH as its own unprivileged account (the installers and the Docker image do).

## Reporting a vulnerability

Please report security problems privately through GitHub's private vulnerability reporting on
<https://github.com/streamzh/streamzh> (Security → Report a vulnerability), not as a public issue. Include the version
(System → Status), how StreamZH is installed (Windows service, Linux, Docker) and the steps to reproduce. Fixes are
released as soon as they are ready and credited in the release notes unless you prefer otherwise.

## What StreamZH protects

| Asset | Where | Who may use it |
|---|---|---|
| Accounts and sessions | `streamzh.db` (passwords as PBKDF2 hashes), sign-in cookie | Owner manages everyone; see the roles below |
| API key | `streamzh.db` (encrypted), Settings → General → API key (owner only) | Admin access for scripts and CloudZH; treat it like a password |
| iCal feed token | `streamzh.db`, Calendar → iCal link | Read-only access to the calendar feed, nothing else |
| Indexer API keys, Usenet and download client passwords, notification secrets | `streamzh.db` | Never sent back to the browser in clear text (shown as `********`) |
| Cookie signing keys | `<data>/keys` | Whoever has them can forge sign-in cookies |
| Backups | `<data>/Backups` and downloaded zips | Contain the database and the keys: everything above |
| Setup code | `<data>/setup-code` until the owner account exists | Lets someone on another device create the owner account |
| Tray token | `<data>/tray.token` | Status, pause/resume, restart and shut down from this computer only |

Roles: **Owner** (everything), **Admin** (settings and users except owners and the owner-only settings below),
**Friend** (library and downloads, no settings), **Requester** (Discover and their own requests only).

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

## Threat model

StreamZH defends against:

- **People on the network without an account.** Every API call needs a sign-in (cookie, Basic or API key), except
  `/health`, the sign-in and setup pages, invitation links and the OpenAPI document.
- **A stranger claiming a fresh installation.** Until the owner account exists, setup from another device needs the
  one-time setup code (see [First-run setup](#first-run-setup)).
- **Password guessing.** Sign-in attempts are rate limited per client and failures are counted per account
  (see [Sign-in limits](#sign-in-limits)).
- **Other web pages acting in your name** (cross-site request forgery, DNS rebinding, clickjacking): see
  [Browser protection](#browser-protection).
- **Users with fewer rights.** Requesters and friends cannot read settings or secrets; a disabled, demoted or signed-out
  user loses access at once, including the live connection.

StreamZH does **not** defend against:

- Somebody with the owner's password, a backup or read access to the data folder: they have full control (the API key gives Admin access).
- The owner running tools: StreamZH runs par2, 7-Zip and ffprobe with its own rights (see [Tools StreamZH runs](#tools-streamzh-runs)).
- Everyone who can reach StreamZH while sign-in is disabled ("Disabled for local addresses" or External without a
  trusted proxy): StreamZH cannot tell them apart.
- A compromised computer or container that StreamZH runs on.

## First-run setup

A new installation has no account. The first person to open the setup page creates the owner account, so StreamZH
makes sure that is you:

- **On the computer StreamZH runs on** (loopback or the computer's own address, no reverse proxy in between) the setup
  page works as it is.
- **From any other device** the setup page asks for the **setup code**. StreamZH creates it at start and shows it
  - in the console and the log (`docker logs streamzh` in Docker),
  - in the file `setup-code` in the data folder (readable by StreamZH's own account only),
  - under **Show setup code** in the Windows tray icon.

The code is no longer needed (and the file is deleted) once the owner account exists. In Docker your browser always
counts as another device.

## Authentication modes and their risks

Settings → General → Security.

| Mode | How it works | Risk |
|---|---|---|
| **Forms** (default) | StreamZH's sign-in page and a cookie | None beyond the passwords you choose |
| **Basic** | The browser's login dialog, same accounts | The password goes with every request: use HTTPS. No sign-out |
| **External** | A reverse proxy in front of StreamZH signs people in (e.g. CloudZH single sign-on); StreamZH lets the proxy's requests in as owner | Anyone who reaches StreamZH in a way that counts as the proxy has full access |
| **Disabled for local addresses** (with Forms or Basic) | Requests from the local networks need no sign-in and act as owner | Everyone on those networks has full access: guests on your Wi-Fi, VPN users, other containers |

Hardening for the sign-in-free modes (Settings → General → Security, Advanced):

- **Trusted local networks**: only these addresses count as local. Empty means all private networks (10.x,
  172.16–31.x, 192.168.x, IPv6 local) and this computer. Set it to your own network, e.g. `192.168.1.0/24`.
- **Trusted proxies**: the addresses of your reverse proxies. In External mode only they may sign people in (empty:
  any local address may, which is how 0.11 and earlier behaved for the local network). Their `X-Forwarded-For` and
  `X-Forwarded-Proto` headers are believed, so sign-in limits count the real client and the cookie is marked Secure
  behind HTTPS. Headers from anybody else change nothing.
- **User header** (External): the header your proxy sets with the signed-in user, e.g. `Remote-User`. Requests without
  it get the normal sign-in page.
- **Allowed host names**: see DNS rebinding below.

**Containers:** with published ports (Docker Desktop, rootless Docker or Podman, port forwarding through NAT) every
visitor may reach StreamZH from an address of the container network and so count as local. StreamZH shows a warning in
the status window when "Disabled for local addresses" is on inside a container without trusted local networks. Keep
sign-in on in containers, or set the trusted local networks to your LAN and make sure the container sees the real
client addresses.

**Locked out?** Start StreamZH once with `--auth-method forms` to get the sign-in page back.

## Browser protection

- **Cross-site requests.** Browsers send cookies, Basic credentials and your network address along with requests that
  other web pages start. StreamZH therefore refuses every changing request (POST, PUT, PATCH, DELETE) unless it shows
  that it comes from StreamZH's own pages: the browser reports it as same-origin (`Sec-Fetch-Site`), its `Origin` is
  StreamZH's own address, or it carries the `X-StreamZH-Request` header that StreamZH's web app sends and other sites
  cannot add. Requests with the API key (`X-Api-Key`) are not affected. The live connection (`/hub`) refuses a foreign
  `Origin`. Backup restores only accept `Content-Type: application/zip`.
- **DNS rebinding.** A web page can make its own host name point at your StreamZH and then talk to it as if it were the
  same site. The sign-in-free modes are therefore given only to requests for host names an attacker cannot control: IP
  addresses, `localhost`, local names (`nas`, `*.local`, `*.lan`, `*.home.arpa`), this computer's own names, the host of
  the application URL and the names under **Allowed host names**. Other names get the normal sign-in page; the log
  says which name to add.
- **Clickjacking and injected content.** Every response carries a Content Security Policy (scripts, styles and
  connections only from StreamZH itself, no framing), `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff` and
  `Referrer-Policy: same-origin`. TMDB images in SVG format are served with `Content-Security-Policy: sandbox`.
- **Cookies.** The sign-in cookie is HttpOnly, SameSite=Strict, limited to StreamZH's URL base, and Secure over HTTPS
  (also behind a trusted proxy that reports HTTPS). Without **Remember me** it ends with the browser session (and after
  24 hours without use); with it, it lasts 30 days while used. Signing out ends the session on the server too, so a
  copy of the cookie stops working. Password changes, role changes, disabling a user and "Sign out everywhere" end all
  of the user's sessions, including open live connections.

## Sign-in limits

- 10 sign-in attempts per minute per client (IPv6: per /64 network), counted separately for the sign-in page, the setup
  page, invitations and password changes.
- 10 failed sign-ins for one account within 15 minutes (from any address) pause sign-in for that account until the
  oldest failure is 15 minutes old; 30 failures from one client pause that client. This applies to the sign-in page and
  to Basic authentication; a browser that already signed in keeps working.
- Behind a reverse proxy all clients share the proxy's address unless the proxy is listed under **Trusted proxies**.

## API access

- Send the API key in the `X-Api-Key` header. Since 0.12, `?apikey=` in the address works only for the live connection
  (`/hub`, where WebSocket clients cannot send headers): keys in addresses end up in browser histories, proxy logs and
  shared links.
- The **iCal feed** has its own read-only token (Calendar → iCal link). Feed links from before 0.12 that contained
  the API key stop working; copy the new link into your calendar app. "Create new link" ends the old one.
- Scripts that call changing endpoints with a cookie or Basic credentials instead of the API key must send the header
  `X-StreamZH-Request: 1`.

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

## Safe deployment

### Reverse proxy and HTTPS

To use StreamZH from outside your home, put it behind a reverse proxy with HTTPS (Caddy, nginx, Traefik, CloudZH) and
do not forward port 9797 on your router. Then:

- bind StreamZH to `127.0.0.1` (Settings → General → Host) or publish the Docker port as `127.0.0.1:9797:9797` when the
  proxy runs on the same host,
- set the **Application URL** to the public address and the **URL base** if the proxy serves StreamZH below a path,
- add the proxy under **Trusted proxies** and let it send `X-Forwarded-For` and `X-Forwarded-Proto`,
- keep Forms authentication, or use External only together with trusted proxies.

### Ports

StreamZH listens on one port (9797 by default; with HTTPS a second one). The built-in torrent engine listens on its own
port for peers. Nothing else is opened.

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
