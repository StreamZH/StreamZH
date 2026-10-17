import { request } from './client'

/** Every API call StreamZH made to an indexer (contract §5). */
export type IndexerEventType = 'query' | 'rss' | 'grab' | 'caps'
export type IndexerEventSource = 'interactive' | 'automatic' | 'rss' | 'search' | 'test'

export interface IndexerHistoryRecord {
  id: number
  date: string
  indexerId: number
  indexer: string
  eventType: IndexerEventType
  successful: boolean
  elapsedMs: number
  source: IndexerEventSource
  /** Human readable, e.g. "Dune: Part Two (2024) tmdbid=693134". */
  query: string | null
  results: number | null
  /** Grab: the release title. */
  title: string | null
  error: string | null
}

export interface IndexerHistoryPage {
  page: number
  pageSize: number
  totalRecords: number
  records: IndexerHistoryRecord[]
}

export interface IndexerHistoryFilter {
  page: number
  pageSize: number
  indexerId?: number
  eventType?: IndexerEventType
}

/** Per-indexer counters and daily totals over a range of days (contract §6). */
export interface IndexerStatsRow {
  indexerId: number
  indexer: string
  queries: number
  rssQueries: number
  grabs: number
  failedQueries: number
  failedGrabs: number
  averageResponseMs: number
}

export interface IndexerStats {
  from: string
  to: string
  indexers: IndexerStatsRow[]
  daily: { date: string; queries: number; grabs: number }[]
}

export const indexerActivityKeys = {
  all: ['indexer-activity'] as const,
  history: (filter: IndexerHistoryFilter) => ['indexer-activity', 'history', filter] as const,
  stats: (days: number) => ['indexer-activity', 'stats', days] as const,
}

export const indexerActivityApi = {
  history: (filter: IndexerHistoryFilter) => {
    const params = new URLSearchParams()
    for (const [key, value] of Object.entries(filter)) {
      if (value != null && value !== '') params.set(key, String(value))
    }
    return request<IndexerHistoryPage>('GET', `/indexer/history?${params}`)
  },
  stats: (days: number) => request<IndexerStats>('GET', `/indexer/stats?days=${days}`),
}
