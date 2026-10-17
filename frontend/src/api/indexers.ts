import { request } from './client'

export interface IndexerStatus {
  healthy: boolean
  /** Only set while the indexer is skipped after failures. */
  disabledTill: string | null
  mostRecentFailure: string | null
  lastError: string | null
  escalationLevel: number
}

export interface Indexer {
  id: number
  name: string
  implementation: string
  baseUrl: string
  apiPath: string
  /** Masked ("********") when a key is stored; send it back unchanged to keep the key. */
  apiKey: string
  categories: number[]
  tvCategories: number[]
  priority: number
  enableRss: boolean
  enableAutomaticSearch: boolean
  enableInteractiveSearch: boolean
  status: IndexerStatus | null
}

export interface IndexerCategory {
  id: number
  name: string
  subCategories: IndexerCategory[]
}

export interface IndexerCapabilities {
  defaultPageSize: number
  maxPageSize: number
  searchAvailable: boolean
  movieSearchParams: string[]
  tvSearchParams: string[]
  categories: IndexerCategory[]
  movieSearchAvailable: boolean
  tvSearchAvailable: boolean
}

export const maskedApiKey = '********'

export const indexersApi = {
  list: () => request<Indexer[]>('GET', '/indexer'),
  schema: () => request<Indexer>('GET', '/indexer/schema'),
  create: (indexer: Indexer) => request<Indexer>('POST', '/indexer', indexer),
  update: (indexer: Indexer) => request<Indexer>('PUT', `/indexer/${indexer.id}`, indexer),
  remove: (id: number) => request<void>('DELETE', `/indexer/${id}`),
  test: (indexer: Indexer) => request<IndexerCapabilities>('POST', '/indexer/test', indexer),
}

export const indexerKeys = {
  list: ['indexers'] as const,
}

/** Standard Newznab names, shown before the indexer's own list is loaded with "Test". */
export const standardCategoryNames: Record<number, string> = {
  2000: 'Movies',
  2010: 'Movies/Foreign',
  2020: 'Movies/Other',
  2030: 'Movies/SD',
  2040: 'Movies/HD',
  2045: 'Movies/UHD',
  2050: 'Movies/BluRay',
  2060: 'Movies/3D',
  2070: 'Movies/DVD',
  2080: 'Movies/WEB-DL',
  2090: 'Movies/x265',
  5000: 'TV',
  5010: 'TV/WEB-DL',
  5020: 'TV/Foreign',
  5030: 'TV/SD',
  5040: 'TV/HD',
  5045: 'TV/UHD',
  5050: 'TV/Other',
  5060: 'TV/Sport',
  5070: 'TV/Anime',
  5080: 'TV/Documentary',
  5090: 'TV/x265',
}
