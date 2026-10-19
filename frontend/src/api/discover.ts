import { tmdbImage } from '../lib/format'
import { request } from './client'
import type { RequestMediaType, RequestStatus } from './requests'

// Discover (0.10): TMDB lists and search for everyone who may request, with the library and request state of every
// title. Contract: the 0.10 brief (agent F builds the backend).

export type DiscoverList = 'trending' | 'popular' | 'upcoming' | 'topRated'

export interface DiscoverStatus {
  inLibrary: boolean
  /** Has a file (movie) or all aired episodes (series). The contract names it available or hasFile. */
  available?: boolean
  hasFile?: boolean
  /** Some but not all episodes on disk. */
  partiallyAvailable?: boolean
  /** State of the open request for this title, if any. */
  requested: RequestStatus | null
  requestId: number | null
  /** The movie/series id in the library, for "Open in library". */
  itemId?: number | null
}

export interface DiscoverItem {
  mediaType: RequestMediaType
  tmdbId: number
  title: string
  year: number | null
  overview: string | null
  posterUrl?: string | null
  posterPath?: string | null
  backdropUrl?: string | null
  backdropPath?: string | null
  rating: number | null
  status: DiscoverStatus
}

export interface DiscoverPage {
  page: number
  totalPages: number
  totalResults?: number
  results: DiscoverItem[]
}

export interface DiscoverSeason {
  seasonNumber: number
  name?: string | null
  episodeCount?: number | null
  airDate?: string | null
  /** Per-season state; the same shape as the title's. */
  status?: Partial<DiscoverStatus> | null
}

export interface DiscoverDetail extends DiscoverItem {
  runtime?: number | null
  genres?: string[]
  releaseDate?: string | null
  network?: string | null
  certification?: string | null
  seasons?: DiscoverSeason[]
}

const plural = (type: RequestMediaType) => (type === 'movie' ? 'movies' : 'series')

export const discoverApi = {
  list: (type: RequestMediaType, list: DiscoverList, page = 1) => request<DiscoverPage>('GET', `/discover/${plural(type)}/${list}?page=${page}`),
  search: (query: string, page = 1) => request<DiscoverPage>('GET', `/discover/search?query=${encodeURIComponent(query)}&page=${page}`),
  detail: (type: RequestMediaType, tmdbId: number) => request<DiscoverDetail>('GET', `/discover/${type}/${tmdbId}`),
}

export const discoverKeys = {
  all: ['discover'] as const,
  list: (type: RequestMediaType, list: DiscoverList) => ['discover', 'list', type, list] as const,
  search: (query: string) => ['discover', 'search', query] as const,
  detail: (type: RequestMediaType, tmdbId: number) => ['discover', 'detail', type, tmdbId] as const,
}

export const listLabels: Record<DiscoverList, string> = {
  trending: 'Trending',
  popular: 'Popular',
  upcoming: 'Upcoming',
  topRated: 'Top rated',
}

export const isAvailable = (s: Partial<DiscoverStatus> | null | undefined) => !!(s && (s.available ?? s.hasFile))

/** Poster for a discover item: the server's URL (image cache) or the TMDB path through StreamZH. */
export const posterSrc = (item: { posterUrl?: string | null; posterPath?: string | null }, size = 'w342') =>
  item.posterUrl || tmdbImage(item.posterPath, size)

export const backdropSrc = (item: { backdropUrl?: string | null; backdropPath?: string | null }) =>
  item.backdropUrl || tmdbImage(item.backdropPath, 'w1280')

export type Badge = { label: string; tone: 'ok' | 'part' | 'lib' | 'req' | 'wait' }

/** The badge on a poster: Available, Partially, In library, Requested, Pending; null when nothing applies. */
export function badgeFor(status: Partial<DiscoverStatus> | null | undefined): Badge | null {
  if (!status) return null
  if (isAvailable(status) || status.requested === 'available') return { label: 'Available', tone: 'ok' }
  if (status.partiallyAvailable || status.requested === 'partiallyAvailable') return { label: 'Partially', tone: 'part' }
  if (status.requested === 'pending') return { label: 'Pending', tone: 'wait' }
  if (status.requested === 'approved' || status.requested === 'processing') return { label: 'Requested', tone: 'req' }
  if (status.inLibrary) return { label: 'In library', tone: 'lib' }
  return null
}
