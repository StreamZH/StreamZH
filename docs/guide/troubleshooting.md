# Troubleshooting and FAQ

First look at the **status window** (bottom right) and **System → Status**: most problems show up there with a link to the
fix. The log is under **System → Logs** (files in `<data>/logs`; Docker: `docker logs streamzh`; Linux:
`journalctl -u streamzh`; Windows: also the event log, Application, source StreamZH).

- [Port already in use](#port-already-in-use)
- [StreamZH cannot be reached from another device](#streamzh-cannot-be-reached-from-another-device)
- [The setup page asks for a setup code](#the-setup-page-asks-for-a-setup-code)
- ["Outside the storage locations"](#outside-the-storage-locations)
- [Folder is read-only or "access denied"](#folder-is-read-only-or-access-denied)
- [Network shares and NAS](#network-shares-and-nas)
- [Nothing gets grabbed](#nothing-gets-grabbed)
- [Downloads fail](#downloads-fail)
- [Imports fail or files are not found](#imports-fail-or-files-are-not-found)
- [Side-by-side mode is on](#side-by-side-mode-is-on)
- [Locked out](#locked-out)
- ["Secret unreadable"](#secret-unreadable)
- ["Program folder not protected"](#program-folder-not-protected)
- [Reporting a bug](#reporting-a-bug)

## Port already in use

StreamZH listens on port 9797. If another program uses it, StreamZH does not start and prints one line instead (also
in the log, in the Windows event log for the service, and in the tray icon):

```text
StreamZH could not start: port 9797 is already in use by another program (is StreamZH already running?). Stop that
program, or choose another port. Start StreamZH with another port, for example --urls http://*:9807 or the environment
variable ASPNETCORE_URLS=http://*:9807, then set that port in Settings → General → Host.
```

- Often it is StreamZH itself, already running (a service and a console window at the same time, or a second tray
  icon start).
- Find the other program: `sudo ss -ltnp 'sport = :9797'` (Linux), `Get-NetTCPConnection -LocalPort 9797` (PowerShell),
  `lsof -i :9797` (macOS).
- Or start StreamZH on another port once: `StreamZH --urls http://*:9807` (or the environment variable
  `ASPNETCORE_URLS=http://*:9807`), open it, and set the port for good in Settings → General → Host. Then start it
  normally again. Remember to change the port of the Windows firewall rule too (`Install-Service.ps1 -Firewall -Port 9807`).
- Docker: keep 9797 inside the container and publish another host port: `-p 9807:9797`.
- The built-in torrent engine's port (51414) is separate; when it is taken the status window says "Torrent port … is
  used by another program". Change it in Settings → Download clients.

## StreamZH cannot be reached from another device

- **Windows**: allow the port in the firewall (installer option, or `Install-Service.ps1 -Firewall`).
- **Bind address** (Settings → General → Host, Advanced) must be `*` or the server's address, not `127.0.0.1`.
- **Docker**: publish the port (`-p 9797:9797`), not only `127.0.0.1:9797:9797`.
- Use the server's IP address (`http://192.168.1.10:9797`) to rule out name resolution.

## The setup page asks for a setup code

On a new installation, only someone on the server itself may create the owner account without a code. From another
device (and always with Docker) the page asks for the **setup code**. Find it in the console or log (`docker logs
streamzh`, `journalctl -u streamzh`), in the file `setup-code` in the data folder, or under **Show setup code** in the
Windows tray icon. It is only needed until the owner account exists. Opened on the server under a name other than
`localhost`, an IP address or the computer's own name, it asks too.

## "Outside the storage locations"

Every folder StreamZH uses must be inside a **storage location** (Settings → General → Storage). The **owner** can
allow a folder in one click ("Allow and add" in the root-folder dialog, "Allow as storage location" in the migration
wizard) or add a location in Settings → General → Storage. An **Admin** cannot; ask the owner.

In Docker, add the paths as the container sees them (`/data/media`, not the host path). Folders inside StreamZH's
data folder (except its own `downloads` folder) and its program folder are always refused.

## Folder is read-only or "access denied"

StreamZH runs under its own account, which needs read and write access to root folders and download folders:

- **Windows service**: the virtual account `NT SERVICE\StreamZH` cannot change folders of your user (Videos, Downloads,
  Desktop) unless they are given to it: run the installer again (page "Folders for the service") or, as
  administrator, `& 'C:\Program Files\StreamZH\tools\Install-Service.ps1' -Folder 'D:\Media'`. With **Controlled folder access**
  on, allow `StreamZH.exe` (installer option or `-AllowControlledFolderAccess`).
- **Linux**: `sudo setfacl -R -m u:streamzh:rwX -m d:u:streamzh:rwX /data/media`, or add `streamzh` to the group
  that owns the folders and restart the service.
- **Docker**: `PUID`/`PGID` must be a user that may write to the mounted folders.

Root folders show **Read only** in Settings → Media Management until this is fixed.

## Network shares and NAS

- **Windows service**: mapped drives (`Z:`) do not exist for services. Use UNC paths (`\\nas\media\movies`). The
  service logs on to the NAS as the computer account; when the NAS wants a user name and password, run the service as
  a user: `& 'C:\Program Files\StreamZH\tools\Install-Service.ps1' -Account .\johndoe` (as administrator), or
  choose **tray icon only**, which runs StreamZH as your user with your mapped drives.
- **Linux**: mount the share (NFS or SMB/CIFS, e.g. in `/etc/fstab`) so that the user `streamzh` may write, for SMB
  with `uid=streamzh,gid=streamzh` or a group it is in.
- **Docker**: mount the share on the host, then pass it into the container as a volume.
- Add the share (or its mount point) as a storage location.
- Hard links do not work across shares or drives; imports then copy.

## Nothing gets grabbed

Work through these:

1. Is the **side-by-side mode** off (Settings → Migration)?
2. Is the movie or episode **monitored**, and is it **released** (movies: minimum availability in Settings → Indexers;
   Wanted → Missing lists what counts as missing)?
3. Is there an enabled **indexer** with RSS and automatic search on, and does **Test** work? Indexers → History shows
   every request.
4. **Manual search** with *Show rejected* tells why each release is not taken: quality not in the profile, wrong
   language, size limits, blocklisted, minimum custom format score, delay, too few seeders …
5. The **Release test** (Tools) explains the decision for any release name.
6. Is the release **pending** because of a Usenet or torrent delay (Settings → Indexers)?

## Downloads fail

- **Missing articles**: the post is older than your provider's retention or was taken down. A backup server (a block
  account at another provider) helps. StreamZH blocklists the release and tries the next one.
- **Usenet server errors**: Settings → Usenet servers → Test. Too many connections (shared with SABnzbd?) cause
  refusals.
- **Password-protected archives** need the password in the NZB; otherwise set *Password-protected RAR* to fail them
  early (Settings → Downloads → Queue, Advanced).
- **par2 or 7-Zip missing**: Settings → Downloads → Tools shows what was found; on Linux and macOS without
  internet access install `par2` and `7zip`/`p7zip` yourself.
- **Low disk space** pauses downloads (Minimum free space, Settings → Downloads).

## Imports fail or files are not found

- **Torrent client on another machine or in another container**: it reports paths StreamZH does not see. Add a
  **remote path mapping** (Settings → Download clients, Advanced).
- **Permissions**: see above. The import needs write access to the download folder and the root folder.
- **Episode title not known yet**: with *Episode title required* the import waits up to 48 hours for TMDB.
- **Not linked to a movie or series** (from the Search page or Add NZB): use **Import…** in Activity.
- **After a migration**: files Radarr or Sonarr know about but StreamZH does not find mean a wrong or missing path
  mapping ([MIGRATION.md](../MIGRATION.md#4-path-mappings)).

## Side-by-side mode is on

The status window says "Side-by-side mode: StreamZH grabs nothing automatically". The migration wizard turns it on so
StreamZH and Radarr/Sonarr do not download the same releases. Searching and grabbing by hand still work. Turn it off
in Settings → Migration → **Switch over…** when you stop the old apps ([MIGRATION.md](../MIGRATION.md#9-switch-over)).

## Locked out

- **Wrong authentication method** (e.g. External without a working proxy): start StreamZH once with
  `--auth-method forms` to get the sign-in page back, then fix Settings → General → Security.
- **"Too many failed sign-ins"**: sign-in is paused for that account from your network for up to 15 minutes after 10 failed
  attempts; wait, or sign in from another network.
- **Sign-in page although "Disabled for local addresses"**: you opened StreamZH by a name it does not trust (`nas`,
  `*.local`, `*.lan`). Add it under **Allowed host names**; the log names the host to add.
- **Forgotten password**: another owner or admin can set a new one in Settings → Users.

## "Secret unreadable"

A stored password or API key was encrypted with sign-in keys this installation does not have (the database was moved or
restored without its `keys` folder, or on another Windows computer). The item works again once you enter the secret
again; the notice links to it. A lost API key is not replaced automatically: create a new one in Settings → General.

## "Program folder not protected"

On Windows, StreamZH found that ordinary users may change its program folder, so anyone could replace `StreamZH.exe`.
Install with the installer (Program Files is protected), or run `tools\Install-Service.ps1` or
`tools\Install-Tray.ps1` from the program folder once as administrator: both protect the folder.

## Reporting a bug

Open an issue on [GitHub](https://github.com/streamzh/streamzh/issues) with the version (System → Status), how
StreamZH is installed, what you did and what happened, and the relevant log lines (Debug level helps: Settings →
General → Logging). **Remove API keys, passwords, host names and anything personal** from logs and screenshots first,
and never attach a backup or the database. Security problems go privately to the maintainers: see
[SECURITY.md](../../SECURITY.md).
