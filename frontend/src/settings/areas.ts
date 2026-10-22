// The settings areas, grouped like the tab rows on the Settings page (Media · Downloads · Connections · General).
// Used by the tabs below the top bar (components/shell/nav.ts), the overview on /settings and the search (Ctrl K).
import type { IconName } from '../components/Icon'

export interface SettingsArea {
  to: string
  label: string
  description: string
  icon: IconName
  /** More words the search (Ctrl K) finds this page by. */
  keywords?: string
}

export interface SettingsGroup {
  key: string
  label: string
  icon: IconName
  areas: SettingsArea[]
  /** Only shown with the Advanced switch on (or while it matters, see useShowMigration). */
  advanced?: boolean
}

export const migrationPath = '/settings/migration'

export const settingsGroups: SettingsGroup[] = [
  {
    key: 'media',
    label: 'Media',
    icon: 'film',
    areas: [
      { to: '/settings/media-management', label: 'Media Management', icon: 'folder', description: 'Root folders, naming, importing, file handling and permissions.' },
      { to: '/settings/profiles', label: 'Profiles', icon: 'star', description: 'Quality profiles: which qualities and languages to grab and until when to upgrade.', keywords: 'trash recyclarr' },
      { to: '/settings/custom-formats', label: 'Custom formats', icon: 'tag', description: 'Conditions on release names, groups, languages and sizes that profiles score, like Radarr and Sonarr.' },
      { to: '/settings/quality', label: 'Quality', icon: 'gauge', description: 'File size limits per quality.' },
      { to: '/settings/import-lists', label: 'Import lists', icon: 'list', description: 'Add movies and series automatically from TMDB lists, Trakt, Plex watchlists and more.' },
      { to: '/settings/metadata', label: 'Metadata', icon: 'filetext', description: 'TMDB, NFO files and artwork next to your media.' },
    ],
  },
  {
    key: 'downloads',
    label: 'Downloads',
    icon: 'download',
    areas: [
      { to: '/settings/indexers', label: 'Indexers', icon: 'filter', description: 'Release rules for all indexers: minimum age, retention, size, RSS sync and release profiles.' },
      { to: '/settings/downloads', label: 'Downloads', icon: 'download', description: 'Download engine, categories, switches, schedule, post-processing and tools.' },
      { to: '/settings/downloadclients', label: 'Download clients', icon: 'share', description: 'Torrent clients (qBittorrent, Transmission, Deluge), the built-in torrent engine and remote path mappings for torrents.' },
      { to: '/settings/usenet', label: 'Usenet servers', icon: 'server', description: 'Your Usenet provider accounts, primary and backup.', keywords: 'nntp provider news' },
    ],
  },
  {
    key: 'connections',
    label: 'Connections',
    icon: 'plug',
    areas: [
      { to: '/settings/requests', label: 'Requests', icon: 'inbox', description: 'Requests from friends and requesters: auto-approve, limits and where approved titles go.' },
      { to: '/settings/discord', label: 'Discord', icon: 'chat', description: 'A built-in Discord bot for requests (replaces Requestrr): slash commands, linked accounts and notifications.', keywords: 'requestrr bot' },
      { to: '/settings/connect', label: 'Connect', icon: 'plug', description: 'Plex, Jellyfin and Emby library updates, and notifications.', keywords: 'notifications plex jellyfin emby webhook' },
    ],
  },
  {
    key: 'general',
    label: 'General',
    icon: 'gear',
    areas: [
      { to: '/settings/general', label: 'General', icon: 'gear', description: 'Host, security, proxy, logging, backups and updates.', keywords: 'port url base api key authentication' },
      { to: '/settings/ui', label: 'UI', icon: 'palette', description: 'Date and time formats, start page and display options.', keywords: 'interface appearance date time' },
      { to: '/settings/users', label: 'Users', icon: 'users', description: 'Accounts, roles, request limits and invite links for friends.', keywords: 'invite friends accounts roles' },
    ],
  },
  {
    key: 'migration',
    label: 'Migration',
    icon: 'mountin',
    advanced: true,
    areas: [
      {
        to: migrationPath,
        label: 'Migration',
        icon: 'mountin',
        description: 'Take over Radarr, Sonarr, Prowlarr and SABnzbd: settings, indexers and your library with its files.',
        keywords: 'radarr sonarr prowlarr sabnzbd import take over side by side switch over',
      },
    ],
  },
]

/** Every settings page, in tab order. */
export const settingsAreas: SettingsArea[] = settingsGroups.flatMap((g) => g.areas)

/** Tools: one page with tabs. The release test keeps its old address below /settings. */
export const toolAreas: SettingsArea[] = [
  { to: '/tools/renamer', label: 'File renamer', icon: 'rename', description: 'Rename many files at once with patterns and a preview.', keywords: 'rename bulk regex' },
  { to: '/settings/release-test', label: 'Release test', icon: 'sparkle', description: 'See how StreamZH reads a release name and whether it would grab it.', keywords: 'parse release name' },
]

/** System pages (like Radarr's System menu). */
export const systemAreas: SettingsArea[] = [
  { to: '/system/status', label: 'Status', icon: 'heart', description: 'Version, health, disk space and runtime.', keywords: 'health version disk' },
  { to: '/system/tasks', label: 'Tasks', icon: 'clock', description: 'Scheduled and queued tasks.', keywords: 'scheduled jobs queue' },
  { to: '/system/backup', label: 'Backup', icon: 'archive', description: 'Backups of the database and settings.', keywords: 'restore' },
  { to: '/system/logs', label: 'Logs', icon: 'logs', description: 'Log entries and log files.', keywords: 'errors debug trace' },
]
