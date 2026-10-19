# Notifications (Connect) and import lists

## Connect

Settings → **Connect** tells your media server about new files and sends you messages. Messages are sent in the
background, in order, and retried when a service is down.

**Add connection** → pick a service → fill in its fields → **Test** → choose the events → Save.

### Media servers

| Service | What it does |
|---|---|
| Plex Media Server | Scans the movie or series folder in the matching Plex library after an import or rename (host, port 32400, Plex token) |
| Jellyfin | Tells Jellyfin which folder changed (or runs a full library scan), so new files show up at once (URL, API key from Dashboard → API Keys) |
| Emby | Like Jellyfin, and can show grabs, imports and health issues to Emby admins |
| Kodi | Updates the Kodi library over JSON-RPC (Kodi: Settings → Services → Control → Allow remote control via HTTP), can clean the library and show notifications on screen |

Media servers react to **On Import**, **On Upgrade** and **On Rename** (Emby and Kodi also to grabs and health
events for their on-screen notifications).

### Notifications

| Service | What it does |
|---|---|
| Discord | Posts a message with poster and details to a channel (a channel webhook URL) |
| Telegram | Sends a message from your own Telegram bot (bot token from @BotFather, chat id) |
| Pushover | Push notifications to your phones and desktops (application token, user key, priority, sound) |
| ntfy | Push notifications through ntfy.sh or your own ntfy server (topics, priority, tags) |
| Gotify | Push notifications through your own Gotify server |
| Email | An email through your mail server (SMTP) |
| Webhook | Every event as JSON to your own URL, in the layout of the Radarr and Sonarr webhooks; see [WEBHOOK.md](../WEBHOOK.md) |
| Apprise | Through an Apprise API server to any of its 100+ services |

### Events

| Event | When |
|---|---|
| On Grab | A release was sent to the downloader |
| On Import | A new file was imported into the library |
| On Upgrade | A better file replaced an existing one |
| On Rename | Files were renamed in the library |
| On Download Failure | A download failed (missing articles, repair or unpacking failed) |
| On Health Issue / On Health Restored | A warning or error appeared in, or went away from, the status window |
| On Request, Request Approved, Request Declined, Request Available | The [request](requests.md) events (notification services only, off by default) |

Passwords, tokens, webhook URLs and custom headers are stored encrypted and shown as `********`. When you change a
connection's address, type its secret again (StreamZH never sends a stored secret to a new address).

## Import lists

Settings → **Import lists** adds movies and series automatically from lists. Each list has its own root folder,
quality profile and monitoring.

| List | Adds |
|---|---|
| TMDB list, collection, person, company, keyword | A public TMDB list; every movie of a collection; the movies or shows of a person, a studio or a keyword |
| TMDB popular and top rated | Popular, top rated, upcoming or now playing, with filters for votes, rating, genre, language and years |
| Trakt | A user's watchlist or public list, or what is popular or trending on Trakt |
| Plex watchlist | Everything you add to your Plex watchlist (app, TV or plex.tv) |
| StreamZH | The movies or series of another StreamZH, e.g. to keep a second server in step |
| RSS list | Any feed whose items carry a TMDB, IMDb or TVDB id |

Per list: what it adds (movies or series; add a list twice for both), **Automatic add** (off: open **Preview** and add
by hand), **Monitor**, root folder, quality profile, minimum availability (movies), **Search on add**. **Preview**
shows what the list would add (will be added, in library, excluded, not on TMDB); **Sync now** reads it at once.

- **Options**: *Clean library* decides what happens to items a list added once they are on no enabled list any more:
  disabled (they stay), log only, keep and unmonitor, or remove (files stay on disk). Items you added yourself are
  never touched, and nothing happens while a list fails. *List update interval* (Advanced): every 24 hours by
  default, 6 to 720.
- **Exclusions**: titles no list may add again (for example ones you removed).
