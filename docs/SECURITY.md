# Security

<!-- FX2 (0.12): the sections below cover roles, owner-only settings, stored secrets, backups and the tools StreamZH runs.
     FX1 creates the overall structure (threat model, deployment, reporting vulnerabilities); merge these sections into it. -->

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
