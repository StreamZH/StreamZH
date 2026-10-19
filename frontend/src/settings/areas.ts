// The settings areas, like Radarr's settings menu. Used by the sidebar (sub-items under Settings) and the overview page.

export interface SettingsArea {
  to: string
  label: string
  description: string
}

export const settingsAreas: SettingsArea[] = [
  { to: '/settings/migration', label: 'Migration', description: 'Take over Radarr, Sonarr, Prowlarr and SABnzbd: settings, indexers and your library with its files.' },
  { to: '/settings/media-management', label: 'Media Management', description: 'Root folders, naming, importing, file handling and permissions.' },
  { to: '/settings/import-lists', label: 'Import lists', description: 'Add movies and series automatically from TMDB lists, Trakt, Plex watchlists and more.' },
  { to: '/settings/profiles', label: 'Profiles', description: 'Quality profiles: which qualities and languages to grab and until when to upgrade.' },
  { to: '/settings/custom-formats', label: 'Custom formats', description: 'Conditions on release names, groups, languages and sizes that profiles score, like Radarr and Sonarr.' },
  { to: '/settings/quality', label: 'Quality', description: 'File size limits per quality.' },
  { to: '/settings/indexers', label: 'Indexers', description: 'Release rules for all indexers: minimum age, retention, size, RSS sync and release profiles.' },
  { to: '/settings/downloads', label: 'Downloads', description: 'Download engine, categories, switches, schedule, post-processing and tools.' },
  { to: '/settings/usenet', label: 'Usenet servers', description: 'Your Usenet provider accounts, primary and backup.' },
  { to: '/settings/connect', label: 'Connect', description: 'Plex, Jellyfin and Emby library updates, and notifications.' },
  { to: '/settings/requests', label: 'Requests', description: 'Requests from friends and requesters: auto-approve, limits and where approved titles go.' },
  { to: '/settings/discord', label: 'Discord', description: 'A built-in Discord bot for requests (replaces Requestrr): slash commands, linked accounts and notifications.' },
  { to: '/settings/metadata', label: 'Metadata', description: 'TMDB, NFO files and artwork next to your media.' },
  { to: '/settings/users', label: 'Users', description: 'Accounts, roles, request limits and invite links for friends.' },
  { to: '/settings/general', label: 'General', description: 'Host, security, proxy, logging, backups and updates.' },
  { to: '/settings/ui', label: 'UI', description: 'Date and time formats, start page and display options.' },
  { to: '/settings/release-test', label: 'Release test', description: 'See how StreamZH reads a release name and whether it would grab it.' },
]

/** System pages (like Radarr's System menu). */
export const systemAreas: SettingsArea[] = [
  { to: '/system/status', label: 'Status', description: 'Version, health, disk space and runtime.' },
  { to: '/system/tasks', label: 'Tasks', description: 'Scheduled and queued tasks.' },
  { to: '/system/backup', label: 'Backup', description: 'Backups of the database and settings.' },
  { to: '/system/logs', label: 'Logs', description: 'Log entries and log files.' },
]
