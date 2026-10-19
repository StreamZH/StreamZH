import { request } from './client'

// Requests (0.10): friends and requesters ask for movies and series, admins approve or decline them. Contract: the
// 0.10 brief (agent F builds the backend).

export type RequestMediaType = 'movie' | 'series'
export type RequestStatus = 'pending' | 'approved' | 'declined' | 'processing' | 'partiallyAvailable' | 'available' | 'failed'
export type RequestSource = 'web' | 'discord' | 'api'

export interface MediaRequest {
  id: number
  mediaType: RequestMediaType
  tmdbId: number
  title: string
  year: number | null
  posterPath: string | null
  /** Optional: a ready image URL from the server (image cache). */
  posterUrl?: string | null
  /** null: all seasons (or a movie). */
  seasons: number[] | null
  status: RequestStatus
  source: RequestSource
  /** The StreamZH user; null for a Discord guest. */
  requestedBy: number | null
  /** Display name or user name of the requester, when the server sends it. */
  requestedByName?: string | null
  discordUserId: string | null
  discordName: string | null
  qualityProfileId: number | null
  rootFolderId: number | null
  /** The movie/series id in the library once it was added. */
  itemId: number | null
  note: string | null
  /** Why it was declined or failed. */
  reason: string | null
  decidedBy: number | null
  decidedByName?: string | null
  created: string
  updated: string
  decided: string | null
  available: string | null
}

export interface RequestPage {
  records: MediaRequest[]
  totalRecords: number
  page: number
  pageSize: number
}

export interface RequestFilter {
  status?: RequestStatus
  mediaType?: RequestMediaType
  /** Only the signed-in user's requests (requesters and friends always get only their own). */
  mine?: boolean
  /** One title (used by the request panel on the library detail pages). */
  tmdbId?: number
  page?: number
  pageSize?: number
}

export interface NewRequest {
  mediaType: RequestMediaType
  tmdbId: number
  seasons?: number[] | 'all'
  note?: string
}

export interface ApproveOptions {
  qualityProfileId?: number | null
  rootFolderId?: number | null
  seasons?: number[]
}

export type SeriesMonitor = 'requested' | 'all' | 'future'

export interface RequestSettings {
  enabled: boolean
  defaultMovieProfileId: number | null
  defaultSeriesProfileId: number | null
  defaultMovieRootFolderId: number | null
  defaultSeriesRootFolderId: number | null
  autoApproveFriends: boolean
  autoApproveRequesters: boolean
  defaultMovieLimit: number | null
  defaultSeriesLimit: number | null
  limitDays: number | null
  searchOnApprove: boolean
  seriesMonitor: SeriesMonitor
  notifyOnAvailable: boolean
}

function query(filter: RequestFilter): string {
  const params = new URLSearchParams()
  if (filter.status) params.set('status', filter.status)
  if (filter.mediaType) params.set('mediaType', filter.mediaType)
  if (filter.mine) params.set('mine', 'true')
  if (filter.tmdbId) params.set('tmdbId', String(filter.tmdbId))
  if (filter.page) params.set('page', String(filter.page))
  if (filter.pageSize) params.set('pageSize', String(filter.pageSize))
  const text = params.toString()
  return text ? `?${text}` : ''
}

/** The count endpoint may answer with a bare number or an object. */
function pendingCount(value: unknown): number {
  if (typeof value === 'number') return value
  if (value && typeof value === 'object') {
    const v = value as { pending?: unknown; count?: unknown }
    if (typeof v.pending === 'number') return v.pending
    if (typeof v.count === 'number') return v.count
  }
  return 0
}

export const requestsApi = {
  list: (filter: RequestFilter = {}) => request<RequestPage>('GET', `/requests${query(filter)}`),
  get: (id: number) => request<MediaRequest>('GET', `/requests/${id}`),
  create: (body: NewRequest) => request<MediaRequest>('POST', '/requests', body),
  approve: (id: number, options: ApproveOptions = {}) => request<MediaRequest>('POST', `/requests/${id}/approve`, options),
  decline: (id: number, reason: string | null) => request<MediaRequest>('POST', `/requests/${id}/decline`, { reason }),
  remove: (id: number) => request<void>('DELETE', `/requests/${id}`),
  retry: (id: number) => request<MediaRequest>('POST', `/requests/${id}/retry`),
  pendingCount: async () => pendingCount(await request<unknown>('GET', '/requests/count')),

  settings: () => request<RequestSettings>('GET', '/settings/requests'),
  saveSettings: (settings: RequestSettings) => request<RequestSettings>('PUT', '/settings/requests', settings),
}

export const requestKeys = {
  all: ['requests'] as const,
  list: (filter: RequestFilter) => ['requests', 'list', filter] as const,
  one: (id: number) => ['requests', 'one', id] as const,
  count: ['requests', 'count'] as const,
  settings: ['settings-requests'] as const,
}

export const statusLabels: Record<RequestStatus, string> = {
  pending: 'Pending',
  approved: 'Approved',
  declined: 'Declined',
  processing: 'Processing',
  partiallyAvailable: 'Partially available',
  available: 'Available',
  failed: 'Failed',
}

/** Open requests can still change (pending, approved, processing, partially available). */
export const isOpen = (status: RequestStatus) => status === 'pending' || status === 'approved' || status === 'processing' || status === 'partiallyAvailable'

/** "Seasons 1, 2" / "All seasons" / "Season 3"; empty for movies. */
export function seasonsText(mediaType: RequestMediaType, seasons: number[] | null): string {
  if (mediaType !== 'series') return ''
  if (!seasons || seasons.length === 0) return 'All seasons'
  const sorted = [...seasons].sort((a, b) => a - b).map((s) => (s === 0 ? 'Specials' : String(s)))
  return sorted.length === 1 ? (seasons[0] === 0 ? 'Specials' : `Season ${sorted[0]}`) : `Seasons ${sorted.join(', ')}`
}

/** Who asked: the user's name, the Discord name of a guest, or a placeholder. */
export function requesterName(r: MediaRequest): string {
  return r.requestedByName || r.discordName || (r.requestedBy == null ? 'Discord guest' : `User ${r.requestedBy}`)
}
