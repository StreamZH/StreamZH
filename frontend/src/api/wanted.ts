import type { QueryClient } from '@tanstack/react-query'
import { request } from './client'
import type { JobStatus } from './library'

export type WantedKind = 'missing' | 'cutoff'
export type WantedType = 'movies' | 'episodes'
export type WantedSortKey = 'title' | 'date' | 'lastSearched' | 'status' | 'profile' | 'quality'
export type SortDirection = 'ascending' | 'descending'

/** On its way: in the download queue, finished and waiting for the import, or (blackhole folder) grabbed in the last 12 hours. */
export type QueueState = 'downloading' | 'waitingForImport' | 'grabbed'

/** Why a file is below the profile (Cutoff unmet). */
export type CutoffReason = 'quality' | 'language' | 'dualLanguage'

/** One row of Wanted. Movies fill the movie fields, episodes the series and episode fields; dates are yyyy-MM-dd. */
export interface WantedRecord {
  type: 'movie' | 'episode'
  id: number
  movieId: number | null
  seriesId: number | null
  episodeId: number | null
  title: string
  year: number
  seasonNumber: number | null
  episodeNumber: number | null
  episodeTitle: string | null
  date: string | null
  inCinemas: string | null
  digitalRelease: string | null
  physicalRelease: string | null
  availableFrom: string | null
  monitored: boolean
  status: 'missing' | 'notAvailable' | 'unaired' | 'cutoffUnmet'
  queue: QueueState | null
  qualityProfileId: number
  profile: string
  quality: string | null
  cutoff: string
  wantedLanguage: string | null
  languages: string[]
  reasons: CutoffReason[]
  lastSearched: string | null
  posterPath: string | null
}

export interface WantedPage {
  page: number
  pageSize: number
  totalRecords: number
  sortKey: WantedSortKey
  sortDirection: SortDirection
  counts: { movies: number; episodes: number }
  records: WantedRecord[]
}

export interface WantedFilter {
  type: WantedType
  page: number
  pageSize: number
  sortKey: WantedSortKey
  sortDirection: SortDirection
  /** true = monitored only (default). */
  monitored: boolean
  /** Missing: true = available (released / aired) only (default). */
  available: boolean
}

export interface WantedSummary {
  missingMovies: number
  missingEpisodes: number
  cutoffUnmetMovies: number
  cutoffUnmetEpisodes: number
}

export type CalendarStatus = 'downloaded' | 'downloading' | 'missing' | 'unaired'

/** One day of the calendar: a movie release (in cinemas, digital, physical) or an episode air date (yyyy-MM-dd, no time). */
export interface CalendarEntry {
  key: string
  type: 'movie' | 'episode'
  releaseType: 'inCinemas' | 'digital' | 'physical' | 'airDate'
  date: string
  movieId: number | null
  seriesId: number | null
  episodeId: number | null
  title: string
  year: number
  seasonNumber: number | null
  episodeNumber: number | null
  episodeTitle: string | null
  overview: string | null
  network: string | null
  monitored: boolean
  hasFile: boolean
  status: CalendarStatus
  queue: QueueState | null
  posterPath: string | null
}

export interface CalendarFilter {
  start: string
  end: string
  unmonitored: boolean
  type: 'all' | 'movies' | 'series'
}

function query(params: Record<string, string | number | boolean>): string {
  return new URLSearchParams(Object.entries(params).map(([k, v]) => [k, String(v)])).toString()
}

export const wantedKeys = {
  all: ['wanted'] as const,
  list: (kind: WantedKind, filter: WantedFilter) => ['wanted', kind, filter] as const,
  summary: ['wanted', 'summary'] as const,
}

export const calendarKeys = {
  all: ['calendar'] as const,
  range: (filter: CalendarFilter) => ['calendar', filter] as const,
}

export const wantedApi = {
  list: (kind: WantedKind, filter: WantedFilter) => request<WantedPage>('GET', `/wanted/${kind}?${query({ ...filter })}`),
  summary: () => request<WantedSummary>('GET', '/wanted/summary'),
  /** Search the selected items, or with all: everything on the list (default filters). */
  search: (body: { kind: WantedKind; type?: WantedType; movieIds?: number[]; episodeIds?: number[]; all?: boolean }) =>
    request<{ queued: number; jobs: JobStatus }>('POST', '/wanted/search', body),
  setMonitored: (movieIds: number[], episodeIds: number[], monitored: boolean) =>
    request<{ changed: number }>('POST', '/wanted/monitor', { movieIds, episodeIds, monitored }),
}

export const calendarApi = {
  entries: (filter: CalendarFilter) => request<CalendarEntry[]>('GET', `/calendar?${query({ ...filter })}`),
}

/** Wanted and Calendar show files, queue states and "last searched": refetch them when the library or a download changes. */
export function invalidateWanted(queryClient: QueryClient) {
  void queryClient.invalidateQueries({ queryKey: wantedKeys.all })
  void queryClient.invalidateQueries({ queryKey: calendarKeys.all })
}
