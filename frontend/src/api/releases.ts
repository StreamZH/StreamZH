import { request } from './client'
import type { JobStatus, MediaType } from './library'

export interface Rejection {
  code: string
  message: string
}

/** One search result, in ranking order (rank 1 = best). Series fields are empty for movies. */
export interface Release {
  guid: string
  indexerId: number
  indexer: string
  title: string
  size: number
  age: number
  ageHours: number
  publishDate: string
  grabs: number | null
  quality: string
  qualityId: number
  languages: string[]
  releaseGroup: string | null
  accepted: boolean
  rejections: Rejection[]
  infoUrl: string | null
  rank: number
  seasonNumber: number | null
  episodeNumbers: number[]
  fullSeason: boolean
  episodeIds: number[]
}

export interface ReleaseSearch {
  /** Indexers asked; 0 means none is enabled for interactive search. */
  indexers: number
  failedIndexers: { indexerId: number; indexer: string; message: string }[]
  releases: Release[]
}

export type SearchTarget =
  | { kind: 'movie'; movieId: number }
  | { kind: 'season'; seriesId: number; seasonNumber: number }
  | { kind: 'episode'; episodeId: number }

export interface GrabResult {
  title: string
  indexer: string
  fileName: string
  folder: string
}

export type CommandName = 'MoviesSearch' | 'MissingMoviesSearch' | 'SeasonSearch' | 'EpisodeSearch'

export interface Command {
  name: CommandName
  movieIds?: number[]
  seriesId?: number
  seasonNumber?: number
  episodeIds?: number[]
}

export type DownloadClient = 'builtin' | 'blackhole'

/** Download settings. Each folder comes with its default and a problem text when StreamZH cannot write there. */
export interface DownloadSettings {
  client: DownloadClient
  nzbFolder: string
  defaultNzbFolder: string
  isDefault: boolean
  problem: string | null
  incompleteFolder: string
  defaultIncompleteFolder: string
  incompleteProblem: string | null
  completeFolder: string
  defaultCompleteFolder: string
  completeProblem: string | null
  /** 0 = unlimited. */
  speedLimitKBps: number
  /** Expert: the queue pauses below this much free space in the incomplete folder (0 = never). */
  minFreeSpaceGb: number
  /** Expert: after a successful unpack the download folder (par2, archives) stays. */
  keepFiles: boolean
  /** Expert: a download fails as soon as it cannot be repaired any more. */
  abortHopeless: boolean
  /** Expert: tries per server and article after timeouts or dropped connections. */
  articleTries: number
}

/** Fields left out keep their value; an empty folder means the default one. */
export interface DownloadSettingsChange {
  client?: DownloadClient
  nzbFolder?: string
  incompleteFolder?: string
  completeFolder?: string
  speedLimitKBps?: number
  minFreeSpaceGb?: number
  keepFiles?: boolean
  abortHopeless?: boolean
  articleTries?: number
}

/** Sent over the hub when an automatic search job is done. */
export interface SearchFinished {
  mediaType: MediaType
  id: number | null
  searched: number
  grabbed: number
  /** Items not searched: already in the download queue, or (NZB folder) grabbed in the last 12 hours. */
  skipped?: number
  skipReason?: 'inQueue' | 'recentlyGrabbed' | null
}

function searchQuery(target: SearchTarget): string {
  switch (target.kind) {
    case 'movie':
      return `movieId=${target.movieId}`
    case 'season':
      return `seriesId=${target.seriesId}&seasonNumber=${target.seasonNumber}`
    case 'episode':
      return `episodeId=${target.episodeId}`
  }
}

export const releaseKeys = {
  search: (target: SearchTarget) => ['release-search', target] as const,
  indexers: ['release-indexers'] as const,
  downloads: ['settings-downloads'] as const,
  searchFinished: (mediaType: MediaType, id: number | null) => ['search-finished', mediaType, id] as const,
}

export const releasesApi = {
  search: (target: SearchTarget) => request<ReleaseSearch>('GET', `/release?${searchQuery(target)}`),
  grab: (indexerId: number, guid: string) => request<GrabResult>('POST', '/release', { indexerId, guid }),
  activeIndexers: () => request<{ automaticSearch: number; interactiveSearch: number }>('GET', '/release/indexers'),
  command: (command: Command) => request<{ name: CommandName; queued: number; jobs: JobStatus }>('POST', '/command', command),
  downloadSettings: () => request<DownloadSettings>('GET', '/settings/downloads'),
  saveDownloadSettings: (change: DownloadSettingsChange) => request<DownloadSettings>('PUT', '/settings/downloads', change),
}

/** "S01E02", "S01E02-E03", "S01 pack". */
export function episodeLabel(release: Pick<Release, 'seasonNumber' | 'episodeNumbers' | 'fullSeason'>): string | null {
  if (release.seasonNumber == null) return null
  const season = `S${String(release.seasonNumber).padStart(2, '0')}`
  if (release.fullSeason) return `${season} pack`
  const numbers = [...release.episodeNumbers].sort((a, b) => a - b)
  if (numbers.length === 0) return season
  const pad = (n: number) => `E${String(n).padStart(2, '0')}`
  return numbers.length === 1 ? season + pad(numbers[0]) : `${season}${pad(numbers[0])}-${pad(numbers[numbers.length - 1])}`
}

export function formatAge(release: Pick<Release, 'age' | 'ageHours'>): string {
  if (release.ageHours < 1) return `${Math.max(1, Math.round(release.ageHours * 60))} min`
  if (release.ageHours < 24) return `${Math.round(release.ageHours)} h`
  return release.age === 1 ? '1 day' : `${release.age} days`
}
