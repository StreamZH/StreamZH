# Searching and grabbing

StreamZH finds releases on your [indexers](indexers.md), decides which one fits each movie or episode, and
**grabs** it: the release is handed to a [download client](downloads.md). Every decision follows the item's
[quality profile](quality.md) and the release rules of Settings → Indexers.

## How releases are found

| Way | When | Setting |
|---|---|---|
| **RSS sync** | Every 60 minutes by default, StreamZH reads the newest releases of every indexer with RSS on and grabs what monitored items need | Settings → Indexers → *RSS sync interval* |
| **Search when adding** | Right after you add a movie or series with *Start search for missing* | Default in Settings → Indexers (Advanced) |
| **Automatic search** | The *Search* button on a movie, series, season or episode; *Search all* on Wanted | |
| **Search for missing** | A scheduled search for all missing items, on top of RSS (costs many API hits) | Settings → Indexers (Advanced), off by default, 6 to 720 hours |
| **Interactive search** | *Manual search* on a detail page: you pick the release | |
| **Import lists** | New items from lists are added and searched | [Import lists](connect.md#import-lists) |

An item that is already in the download queue (with the blackhole folder: grabbed in the last 12 hours) is not
searched again; use Manual search to grab another release.

## Interactive search

**Manual search** on a movie, season or episode asks all indexers with interactive search on (this can take up to a
minute when an indexer is slow) and lists the releases, best first. Sort by best match, seeders, grabs, format score
or preferred score. Each row shows quality, languages, size, age, indexer, custom formats with their score and, for
torrents, seeders and peers.

**Show rejected** also lists releases StreamZH would not grab, each with the reason (wrong quality, too big, too
young, blocklisted, below the minimum format score, …). **Grab anyway** takes such a release after a confirmation.

**Indexers → Search** searches all indexers for any text without adding anything to the library, by type,
category and indexer. Grabs from there go to the download queue but are not linked to a movie or series (import
them with *Import…* in Activity).

## How StreamZH picks a release

1. Releases that break a rule are rejected: quality not allowed in the profile, wrong language, outside the size
   limits (Settings → Quality), older than your Usenet retention or bigger than *Maximum size*, too young (*Minimum
   age*), hardcoded subtitles, a release profile's *must (not) contain*, blocklisted, fewer seeders than the
   indexer's *Minimum seeders*, below the profile's *minimum custom format score*, not an upgrade of the file you have.
2. The rest are ranked, the first difference wins: quality (qualities of one group rank equally), PROPER/REPACK,
   custom format score, dual language (when the profile prefers it), the release profiles' preferred score, the
   preferred protocol, indexer priority, seeders and peers (torrents), size closest to the preferred size, and the
   newest upload.
3. The best one is grabbed.

**Tools → Release test** shows this for any release name: paste it, optionally pick a movie or series, and StreamZH
explains how it reads the name (title, year, season and episode, quality, languages, group, custom formats) and
whether it would grab it, and why not.

## Usenet or torrent: preference and delays

Settings → Indexers → **Usenet and torrents**:

- **Preferred protocol** (Usenet, Torrent, No preference): which one wins when quality and score are equal.
- **Usenet delay / Torrent delay** (Advanced): a new release waits this long before it may be grabbed, giving a
  better release (or one of the preferred protocol) time to show up. Waiting releases are **pending**; the task
  *Process pending releases* grabs them when their time is up.
- **Skip delay for the best quality** (Advanced): a release with the highest quality the profile allows is grabbed at
  once.

## Wanted

**Wanted** (sidebar) has two lists:

- **Missing**: monitored movies and episodes without a file that are released (movies by their minimum
  availability, episodes by their air date). *Show not yet available* and *Show unmonitored* widen the list.
- **Cutoff unmet**: files below the cutoff of their profile, in the wrong language, without the dual language the
  profile prefers, or below the profile's *Upgrade until score*.

**Search all missing** / **Search all cutoff unmet** search everything on the list in the background. Select rows to
search or unmonitor only those.

## Failed downloads and the blocklist

When a download fails (missing articles par2 cannot repair, a broken archive, a password StreamZH does not know) or
its import fails:

- with **Redownload failed** on (Settings → Media Management, default), the release goes on the **blocklist** and
  StreamZH searches for another one at once;
- *Remove failed downloads* (Advanced) removes them from Activity and deletes their incomplete files.

**Activity → Blocklist** lists the blocked releases with the reason. Remove one to allow it again, or **Clear all**.
Blocklisted releases are never grabbed by automatic searches; Manual search shows them as rejected.

## Side-by-side mode

While you migrate from Radarr and Sonarr, the **side-by-side mode** (Settings → Migration) stops StreamZH from grabbing
anything by itself: no RSS sync, no automatic or scheduled searches, no searches after failures or from import lists.
Searching and grabbing by hand still work. See [MIGRATION.md](../MIGRATION.md#3-switch-on-the-side-by-side-mode).
