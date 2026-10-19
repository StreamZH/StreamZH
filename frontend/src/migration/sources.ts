import type { MigrationRun, MigrationSource } from '../api/migration'
import type { IconName } from '../components/Icon'

// What the wizard says about each old app: what it brings, where its API key is, and where the taken-over parts end up.

export interface SourceMeta {
  key: MigrationSource
  label: string
  icon: IconName
  brings: string
  /** Where the API key is in the old app. */
  keyHint: string
  port: number
  optional?: boolean
  optionalHint?: string
}

/** In the recommended order: Radarr → Sonarr → (Prowlarr) → SABnzbd. */
export const sources: SourceMeta[] = [
  {
    key: 'radarr',
    label: 'Radarr',
    icon: 'film',
    brings: 'Root folders, quality profiles, custom formats, naming, indexers and your movies with their files.',
    keyHint: 'Settings → General → Security',
    port: 7878,
  },
  {
    key: 'sonarr',
    label: 'Sonarr',
    icon: 'tv',
    brings: 'Root folders, quality and release profiles, naming, indexers and your series with their episodes and files.',
    keyHint: 'Settings → General → Security',
    port: 8989,
  },
  {
    key: 'prowlarr',
    label: 'Prowlarr',
    icon: 'network',
    brings: 'All your indexers with their categories and limits.',
    keyHint: 'Settings → General → Security',
    port: 9696,
    optional: true,
    optionalHint: 'Only if you use it; otherwise the indexers come from Radarr and Sonarr.',
  },
  {
    key: 'sabnzbd',
    label: 'SABnzbd',
    icon: 'download',
    brings: 'Usenet servers, categories, speed limit, schedule and download switches.',
    keyHint: 'Config → General → Security',
    port: 8080,
  },
]

export const sourceMeta = (key: string): SourceMeta | undefined => sources.find((s) => s.key === key)

export const sourceLabel = (key: string) => sourceMeta(key)?.label ?? key

/** Sources taken over for real (a finished run that was not a dry run). */
export function migratedSources(runs: MigrationRun[] | undefined): Set<string> {
  return new Set((runs ?? []).filter((r) => r.status === 'done' && !r.dryRun).map((r) => r.source))
}

/** The next source in the recommended order that was not taken over yet (Prowlarr is optional and never "next"). */
export function nextSource(runs: MigrationRun[] | undefined, after?: string): SourceMeta | undefined {
  const done = migratedSources(runs)
  if (after) done.add(after)
  return sources.find((s) => !done.has(s.key) && !s.optional)
}

/** Where the parts of a source end up in StreamZH, for the links in the report. */
export const sectionLinks: Record<string, { to: string; label: string }> = {
  rootFolders: { to: '/settings/media-management', label: 'Media Management' },
  naming: { to: '/settings/media-management', label: 'Media Management' },
  mediaManagement: { to: '/settings/media-management', label: 'Media Management' },
  qualityDefinitions: { to: '/settings/quality', label: 'Quality' },
  customFormats: { to: '/settings/custom-formats', label: 'Custom formats' },
  qualityProfiles: { to: '/settings/profiles', label: 'Profiles' },
  releaseProfiles: { to: '/settings/indexers', label: 'Indexer settings' },
  indexers: { to: '/indexers', label: 'Indexers' },
  downloadClients: { to: '/settings/downloadclients', label: 'Download clients' },
  servers: { to: '/settings/usenet', label: 'Usenet servers' },
  categories: { to: '/settings/downloads', label: 'Downloads' },
  general: { to: '/settings/downloads', label: 'Downloads' },
  downloads: { to: '/settings/downloads', label: 'Downloads' },
  notifications: { to: '/settings/connect', label: 'Connect' },
  importListExclusions: { to: '/settings/import-lists', label: 'Import lists' },
  movies: { to: '/library/movies', label: 'Movies' },
  series: { to: '/library/series', label: 'Series' },
  history: { to: '/activity', label: 'Activity' },
  blocklist: { to: '/activity', label: 'Activity' },
}

// ---- Remembered per browser: the address of each old app and the path mappings (never the API key) ----

const urlKey = (source: string) => `streamzh-migration-url-${source}`
const mapKey = (source: string) => `streamzh-migration-paths-${source}`

export function rememberedUrl(source: string): string {
  try {
    return localStorage.getItem(urlKey(source)) ?? ''
  } catch {
    return ''
  }
}

export function rememberUrl(source: string, url: string) {
  try {
    localStorage.setItem(urlKey(source), url.trim())
  } catch {
    // no storage: the address has to be typed again next time
  }
}

export function rememberedMappings(source: string): { remote: string; local: string }[] {
  try {
    const value = JSON.parse(localStorage.getItem(mapKey(source)) ?? '[]') as unknown
    return Array.isArray(value)
      ? value.filter((v): v is { remote: string; local: string } => typeof v?.remote === 'string' && typeof v?.local === 'string')
      : []
  } catch {
    return []
  }
}

export function rememberMappings(source: string, mappings: { remote: string; local: string }[]) {
  try {
    localStorage.setItem(mapKey(source), JSON.stringify(mappings))
  } catch {
    // no storage
  }
}

/** Dates from the API may come without a zone; they are UTC. */
export function utc(value: string): Date {
  return new Date(/[zZ]|[+-]\d\d:?\d\d$/.test(value) ? value : `${value}Z`)
}
