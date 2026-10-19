import { request } from './client'

export type MinimumAvailability = 'announced' | 'inCinemas' | 'released'

/** 0.11: the protocol that wins between equally good releases. */
export type PreferredProtocol = 'usenet' | 'torrent' | 'noPreference'

/** Settings → Indexers: release rules for all indexers and automation. 0 = off or unlimited. */
export interface IndexerOptions {
  /** Minutes. */
  minimumAge: number
  /** Days. */
  retention: number
  /** MB. */
  maximumSize: number
  allowHardcodedSubs: boolean
  whitelistedHardcodedSubs: string[]
  minimumAvailability: MinimumAvailability
  /** Days, may be negative. */
  availabilityDelay: number
  /** Minutes: 0 or 10 to 120. */
  rssSyncInterval: number
  /** Hours: 0 or 6 to 720. */
  missingSearchInterval: number
  searchOnAdd: boolean
  /** Text searches also use alternative titles (German title, scene names, yours), at most three per search. */
  searchAlternativeTitles: boolean
  /** 0.11 (missing on older servers): preferred protocol and delays, like Radarr's delay profile. */
  preferredProtocol?: PreferredProtocol
  /** Minutes a new Usenet release waits before RSS or automatic search may grab it. 0 = no delay. */
  usenetDelay?: number
  /** Minutes a new torrent release waits. 0 = no delay. */
  torrentDelay?: number
  /** Skip the delay when the release already has the profile's highest quality (Radarr's "bypass if highest quality"). */
  bypassDelayIfHighestQuality?: boolean
}

export interface PreferredTerm {
  term: string
  score: number
}

/** Sonarr-style release profile. Empty indexerIds / qualityProfileIds = all. */
export interface ReleaseProfile {
  id: number
  name: string
  enabled: boolean
  required: string[]
  ignored: string[]
  preferred: PreferredTerm[]
  indexerIds: number[]
  qualityProfileIds: number[]
}

export const emptyReleaseProfile: ReleaseProfile = {
  id: 0,
  name: '',
  enabled: true,
  required: [],
  ignored: [],
  preferred: [],
  indexerIds: [],
  qualityProfileIds: [],
}

/** A scheduled task as System → Tasks lists it (only the fields this page shows). */
export interface ScheduledTask {
  key: string
  name: string
  intervalMinutes: number | null
  lastRun: string | null
  nextRun: string | null
  lastSuccess: boolean | null
  lastError: string | null
  running: boolean
  queued: boolean
}

export const indexerOptionsKeys = {
  options: ['settings', 'indexers'] as const,
  releaseProfiles: ['release-profiles'] as const,
  tasks: ['system', 'tasks'] as const,
}

export const indexerOptionsApi = {
  options: () => request<IndexerOptions>('GET', '/settings/indexers'),
  saveOptions: (options: IndexerOptions) => request<IndexerOptions>('PUT', '/settings/indexers', options),
  releaseProfiles: () => request<ReleaseProfile[]>('GET', '/releaseprofile'),
  saveReleaseProfile: (profile: ReleaseProfile) =>
    profile.id > 0
      ? request<ReleaseProfile>('PUT', `/releaseprofile/${profile.id}`, profile)
      : request<ReleaseProfile>('POST', '/releaseprofile', profile),
  removeReleaseProfile: (id: number) => request<void>('DELETE', `/releaseprofile/${id}`),
  tasks: () => request<ScheduledTask[]>('GET', '/system/tasks'),
  runTask: (key: string) => request<{ queued: boolean }>('POST', `/system/tasks/${encodeURIComponent(key)}/run`),
}

/** "in 12 min", "3 h ago", "just now". */
export function relativeTime(value: string | null, now = Date.now()): string | null {
  if (!value) return null
  const minutes = Math.round((new Date(value).getTime() - now) / 60_000)
  const abs = Math.abs(minutes)
  if (abs < 1) return 'just now'
  const text = abs < 60 ? `${abs} min` : abs < 48 * 60 ? `${Math.round(abs / 60)} h` : `${Math.round(abs / 1440)} days`
  return minutes > 0 ? `in ${text}` : `${text} ago`
}

/** A term is a regex when written as /pattern/ or /pattern/i (like Sonarr). */
export function isRegexTerm(term: string): boolean {
  return term.length >= 2 && term.startsWith('/') && term.lastIndexOf('/') > 0
}

/**
 * Quick check in the browser, so mistakes show while typing; the server checks again with .NET's regex engine.
 * Returns null when the term looks fine.
 */
export function checkTerm(term: string): string | null {
  const trimmed = term.trim()
  if (!trimmed) return 'Empty terms are not allowed.'
  if (trimmed.length > 200) return 'Terms can have at most 200 characters.'
  if (!isRegexTerm(trimmed)) return null
  const end = trimmed.lastIndexOf('/')
  const pattern = trimmed.slice(1, end)
  const flags = trimmed.slice(end + 1)
  if (!pattern) return `'${trimmed}' is an empty regular expression.`
  if ([...flags].some((f) => f !== 'i')) return `'${trimmed}': only the flag i (ignore case) is supported.`
  try {
    new RegExp(pattern, flags)
    return null
  } catch (e) {
    return `'${trimmed}' is not a valid regular expression: ${e instanceof Error ? e.message : String(e)}`
  }
}

export function formatScore(score: number): string {
  return score > 0 ? `+${score}` : String(score)
}
