import { request } from './client'
import type { MediaType } from './library'

export interface HistoryRecord {
  id: number
  date: string
  eventType: 'Grabbed'
  mediaType: MediaType | null
  movieId: number | null
  seriesId: number | null
  episodeIds: number[]
  sourceTitle: string
  quality: string
  qualityId: number
  languages: string[]
  releaseGroup: string | null
  indexer: string | null
  indexerId: number | null
  size: number
  data: Record<string, string | null>
}

export interface HistoryPage {
  page: number
  pageSize: number
  totalRecords: number
  records: HistoryRecord[]
}

export interface HistoryFilter {
  page?: number
  pageSize?: number
  movieId?: number
  seriesId?: number
}

export const historyKeys = {
  all: ['history'] as const,
  list: (filter: HistoryFilter) => ['history', filter] as const,
}

export const historyApi = {
  page: (filter: HistoryFilter = {}) => {
    const params = new URLSearchParams()
    for (const [key, value] of Object.entries(filter)) {
      if (value != null) params.set(key, String(value))
    }
    return request<HistoryPage>('GET', `/history?${params}`)
  },
}
