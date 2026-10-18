import { request } from './client'
import type { FormatMediaType } from './customFormats'
import type { QualityProfile } from './profiles'

/** TRaSH Guides sync (the Recyclarr part): status, templates, format groups, applying a template to a profile. */
export interface TrashStatus {
  enabled: boolean
  repository: string
  branch: string
  lastSync: string | null
  commit: string | null
  error: string | null
  counts: { movieFormats: number; seriesFormats: number; movieProfiles: number; seriesProfiles: number }
}

export interface TrashProfileSummary {
  trashId: string
  name: string
  slug: string
  /** Group from quality-profile-groups: Standard, Anime, French, German, … */
  group: string
  /** May contain <br> line breaks (TRaSH JSON). */
  description: string
  url: string | null
  language?: string | null
}

export interface TrashProfileItem {
  name: string
  allowed: boolean
  /** Qualities of a group; empty or missing for a single quality. */
  items?: string[]
}

export interface TrashProfileDetail extends TrashProfileSummary {
  upgradeAllowed?: boolean
  cutoff?: string
  minFormatScore?: number
  cutoffFormatScore?: number
  minUpgradeFormatScore?: number
  items?: TrashProfileItem[]
  formats?: { trashId: string; name: string; score: number }[]
}

export interface TrashFormat {
  trashId: string
  name: string
  description?: string | null
  defaultScore: number
  groups: string[]
  installed: boolean
}

export interface TrashGroup {
  trashId: string
  name: string
  description: string
  /** TRaSH marks some groups as default ("default": "true"). */
  default?: boolean
  /** Templates (trash ids) the group is meant for; when missing the group is offered for every template. */
  profiles?: string[]
  formats: { trashId: string; name: string; required: boolean; default: boolean }[]
}

export interface TrashNaming {
  movieFolder?: string
  movieFile?: string
  seriesFolder?: string
  seriesFile?: string
}

export interface TrashApplyRequest {
  /** null creates a new profile. */
  profileId: number | null
  name?: string
  movieTemplate?: string
  seriesTemplate?: string
  extraGroups?: string[]
  applyQualitySizes: boolean
  naming?: TrashNaming
}

export interface TrashApplyResult {
  profile: QualityProfile
  created: number
  updated: number
}

export interface TrashSettings {
  enabled: boolean
  repository: string
  branch: string
  syncIntervalHours: number
  /** Managed formats and template profiles follow the guides. */
  autoUpdate: boolean
  /** Scores changed by hand survive updates from the guides. */
  keepScoreOverrides: boolean
}

export interface RecyclarrResult {
  profiles: { name: string; template?: string | null; formats: number; overrides: number }[]
  warnings: string[]
}

export const trashKeys = {
  status: ['trash-status'] as const,
  settings: ['trash-settings'] as const,
  profiles: (mediaType: FormatMediaType) => ['trash-profiles', mediaType] as const,
  profile: (mediaType: FormatMediaType, trashId: string) => ['trash-profile', mediaType, trashId] as const,
  formats: (mediaType: FormatMediaType) => ['trash-formats', mediaType] as const,
  groups: (mediaType: FormatMediaType) => ['trash-groups', mediaType] as const,
}

export const trashApi = {
  status: () => request<TrashStatus>('GET', '/trash/status'),
  sync: () => request<TrashStatus>('POST', '/trash/sync'),
  profiles: (mediaType: FormatMediaType) => request<TrashProfileSummary[]>('GET', `/trash/profiles?mediaType=${mediaType}`),
  profile: (mediaType: FormatMediaType, trashId: string) =>
    request<TrashProfileDetail>('GET', `/trash/profiles/${encodeURIComponent(trashId)}?mediaType=${mediaType}`),
  formats: (mediaType: FormatMediaType) => request<TrashFormat[]>('GET', `/trash/formats?mediaType=${mediaType}`),
  groups: (mediaType: FormatMediaType) => request<TrashGroup[]>('GET', `/trash/groups?mediaType=${mediaType}`),
  apply: (body: TrashApplyRequest) => request<TrashApplyResult>('POST', '/trash/apply', body),
  settings: () => request<TrashSettings>('GET', '/settings/trash'),
  saveSettings: (settings: TrashSettings) => request<TrashSettings>('PUT', '/settings/trash', settings),
  recyclarr: (yaml: string, apply: boolean) => request<RecyclarrResult>('POST', '/trash/recyclarr', { yaml, apply }),
}

/** TRaSH descriptions come with <br> and the odd tag: plain lines for the UI. */
export function plainLines(text: string | null | undefined): string[] {
  if (!text) return []
  return text
    .replace(/<br\s*\/?>/gi, '\n')
    .replace(/<[^>]+>/g, '')
    .replace(/&amp;/g, '&')
    .split('\n')
    .map((l) => l.trim().replace(/^[-•*]\s*/, ''))
    .filter(Boolean)
}

/** "German Quality Profile that covers: WEBDL: 720p, 1080p · Bluray: 720p, 1080p". */
export function shortDescription(text: string | null | undefined, maxLines = 4): string {
  const lines = plainLines(text).slice(0, maxLines)
  if (lines.length > 1 && lines[0].endsWith(':')) return `${lines[0]} ${lines.slice(1).join(' · ')}`
  return lines.join(' · ')
}

/** "[Optional] Golden Rule HD" → { category: 'Optional', name: 'Golden Rule HD' }. */
export function splitGroupName(name: string): { category: string; name: string } {
  const m = /^\[([^\]]+)\]\s*(.*)$/.exec(name)
  return m ? { category: m[1], name: m[2] || m[1] } : { category: 'Other', name }
}

/** Naming variants of the TRaSH Guides (keys of radarr-naming.json / sonarr-naming.json). */
export const namingVariants = {
  movieFolder: ['default', 'plex-imdb', 'plex-tmdb', 'emby-imdb', 'emby-tmdb', 'jellyfin-imdb', 'jellyfin-tmdb'],
  movieFile: [
    'standard',
    'plex-imdb',
    'plex-tmdb',
    'plex-anime-imdb',
    'plex-anime-tmdb',
    'plex-edition-alt-imdb',
    'plex-edition-alt-tmdb',
    'emby-imdb',
    'emby-tmdb',
    'emby-anime-imdb',
    'emby-anime-tmdb',
    'jellyfin-imdb',
    'jellyfin-tmdb',
    'jellyfin-anime-imdb',
    'jellyfin-anime-tmdb',
    'original',
    'p2p-scene',
  ],
  seriesFolder: ['default', 'plex-imdb', 'plex-tvdb', 'emby-imdb', 'emby-tvdb', 'jellyfin-tvdb'],
  seriesFile: ['default', 'original', 'p2p-scene'],
} as const

/** One choice for the simple wizard; the expert view sets each key on its own. */
export const namingPresets: { key: string; label: string; naming: TrashNaming | undefined }[] = [
  { key: 'keep', label: 'Keep my naming', naming: undefined },
  { key: 'standard', label: 'TRaSH standard', naming: { movieFolder: 'default', movieFile: 'standard', seriesFolder: 'default', seriesFile: 'default' } },
  { key: 'plex', label: 'Plex (IMDb ids)', naming: { movieFolder: 'plex-imdb', movieFile: 'plex-imdb', seriesFolder: 'plex-imdb', seriesFile: 'default' } },
  { key: 'plex-tmdb', label: 'Plex (TMDB / TVDB ids)', naming: { movieFolder: 'plex-tmdb', movieFile: 'plex-tmdb', seriesFolder: 'plex-tvdb', seriesFile: 'default' } },
  { key: 'emby', label: 'Emby', naming: { movieFolder: 'emby-tmdb', movieFile: 'emby-tmdb', seriesFolder: 'emby-tvdb', seriesFile: 'default' } },
  { key: 'jellyfin', label: 'Jellyfin', naming: { movieFolder: 'jellyfin-tmdb', movieFile: 'jellyfin-tmdb', seriesFolder: 'jellyfin-tvdb', seriesFile: 'default' } },
]
