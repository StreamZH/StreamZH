# Security

StreamZH controls your media library, your indexer and Usenet accounts and the computer it runs on (it starts tools
such as par2 and 7-Zip and writes into your media folders). This page says what StreamZH protects, what it relies on
you for, and how to run it safely. Short version:

- Keep sign-in on (the default). Use "Disabled for local addresses" only on a home network you trust.
- Reach StreamZH from the internet only through a reverse proxy with HTTPS, never by forwarding port 9797.
- Treat the API key, backups and the data folder like passwords.
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
| API key | `streamzh.db`, Settings → General → API key | Full access (scripts, CloudZH); treat it like the owner's password |
| iCal feed token | `streamzh.db`, Calendar → iCal link | Read-only access to the calendar feed, nothing else |
| Indexer API keys, Usenet and download client passwords, notification secrets | `streamzh.db` | Never sent back to the browser in clear text (shown as `********`) |
| Cookie signing keys | `<data>/keys` | Whoever has them can forge sign-in cookies |
| Backups | `<data>/Backups` and downloaded zips | Contain the database and the keys: everything above |
| Setup code | `<data>/setup-code` until the owner account exists | Lets someone on another device create the owner account |
| Tray token | `<data>/tray.token` | Status, pause/resume, restart and shut down from this computer only |

Roles: **Owner** (everything; the only role that manages owners and creates a new API key), **Admin** (settings and users except
owners), **Friend** (library and downloads, no settings), **Requester** (Discover and their own requests only).

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

- Somebody with the owner's password, the API key, a backup or read access to the data folder: they have full control.
- An owner or admin who points a tool path at another program: StreamZH runs it as its own account.
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

### Service accounts and the data folder

Run StreamZH as its own account without administrator or root rights; the Linux installer and the Docker image do (the
container starts as root only to hand `/config` to `PUID:PGID`). The data folder holds the database with every secret
and the cookie keys: only StreamZH's account (and administrators) should be able to read it.

### Backups are secret material

A backup contains the whole database (API key, indexer keys, Usenet and download client passwords) and the cookie
keys. Store backups like passwords, do not share them, and delete old copies you downloaded.
