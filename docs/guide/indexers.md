# Indexers

Indexers are the sites StreamZH searches for releases: **Newznab** indexers for Usenet (NZB files) and **Torznab**
indexers for torrents (directly, or through Jackett or Prowlarr). StreamZH has Prowlarr's job built in, so one
indexer serves movies and series. StreamZH ships no indexers: add only sources you are permitted to use.

**Indexers** in the sidebar (Downloads group) lists your indexers with their state, protocol and what they are used
for. The tabs **Search**, **Stats** and **History** are below.

## Add an indexer

**Add indexer** opens the catalog of known indexers. Filter it by protocol, language, privacy (private,
semi-private, public) and categories, or pick **Generic Newznab** or **Generic Torznab** for one that is not listed.
Then fill in the form and press **Test**: StreamZH connects, reads what the indexer can search (its capabilities)
and loads its categories.

| Field | Meaning |
|---|---|
| Name | Shown in search results and history |
| Enable | Off: StreamZH never uses this indexer, not even for interactive search |
| URL | The indexer's own address (catalog entries fill it in; then it is Advanced unless it differs). Torznab through Jackett: the Torznab feed URL Jackett shows |
| API path (Advanced) | Usually `/api` |
| API key | From your account or profile page on the indexer's website. Stored encrypted, never shown again |
| Additional parameters (Advanced) | Added to every request, e.g. `&extended=1` |
| VIP expiration (Advanced) | When your paid membership ends. StreamZH warns 7 days before |
| Query limit, Grab limit, Limits unit (Advanced) | The API limits of your account, counted over the last 24 hours or the last hour. When a limit is reached, the indexer is skipped until the count drops |
| Indexer priority (Advanced) | 1 to 50, 1 is the highest. Breaks ties between equally good releases |
| Minimum seeders (Torznab) | Releases with fewer seeders are rejected: they would hardly download |
| Seed ratio, Seed time (Torznab) | Seed goals for torrents from this indexer. Empty: the download client's default. Private trackers often ask for 72 hours or more |
| Season-pack seed time (Torznab, Advanced) | Seed time for whole seasons. Empty: the seed time above |
| Use for | **RSS** watches for new releases, **Automatic search** runs on its own, **Interactive search** is the Manual search button |
| Categories (Advanced) | Movie and TV categories to search (standard Newznab categories; Test loads the indexer's own list) |

A failing indexer is skipped for a while (5 minutes, growing up to 24 hours after repeated failures) and tried again
later; the status window says so.

**Test all** tests every enabled indexer at once. The details view of an indexer shows its search capabilities
(search by TMDB, IMDb, TVDB id or only by text), its categories and its limits, and has its own **Test** button. A
successful test also ends a waiting time after earlier failures.

An indexer that still goes through Prowlarr (an import from an older version) is marked in its details;
**Use the indexer's own address** switches it to the provider's address, then enter its API key.

## Import from Prowlarr

**Import from Prowlarr** reads all indexers from Prowlarr (address as seen from the StreamZH server, plus Prowlarr's
API key from Settings → General) with their categories, priority, limits, VIP date and seed settings. Nothing
changes in Prowlarr; indexers that are already in StreamZH are skipped.

Every indexer with an API of its own gets the **provider's own address** (from Prowlarr, or from the catalog when
Prowlarr does not show it), never Prowlarr's address. Prowlarr does not hand out the indexers' API keys: paste each
key (from your account page at the indexer) in the import. An indexer without a key is added **switched off**; enter
its key under Edit and switch it on. Only indexers without an API of their own (site scrapers) are searched through
Prowlarr, which then has to keep running. Test the imported indexers once to load their categories and capabilities.

The [migration wizard](../MIGRATION.md#prowlarr) does the same as part of a full take-over.

## Search, Stats and History

- **Search**: search all your indexers at once for any text, without adding anything to the library. Narrow it down
  by type (movies, series), categories and indexers; leave the text empty to list the newest releases. Grab sends a
  result to the download queue.
- **Stats**: queries, RSS syncs, grabs and failures per indexer and per day, and average response times.
- **History**: every request StreamZH sent to an indexer (search, RSS, grab, capabilities, test), newest first,
  filterable by indexer and event.

## Release rules for all indexers

Settings → **Indexers** holds the rules that apply to every indexer: retention, maximum size, minimum age,
hardcoded subtitles, minimum availability of movies, Usenet/torrent preference and delays, RSS sync and the
scheduled search, and release profiles. See the [settings reference](settings.md#indexers).
