import { request } from './client'

export type IndexerProtocol = 'usenet' | 'torrent'
export type IndexerPrivacy = 'private' | 'semiPrivate' | 'public'
export type LimitsUnit = 'day' | 'hour'

export interface IndexerStatus {
  healthy: boolean
  /** Only set while the indexer is skipped after failures. */
  disabledTill: string | null
  mostRecentFailure: string | null
  lastError: string | null
  escalationLevel: number
  /** API queries in the current limits window (rolling last 24 h or last hour). */
  queriesUsed: number
  /** NZB downloads in the current limits window. */
  grabsUsed: number
  limitReached: 'query' | 'grab' | null
  /** Null without a VIP expiration date; negative once it has expired. */
  vipExpiresInDays: number | null
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
  /** Master switch: a disabled indexer is never used. */
  enable: boolean
  /** Catalog entry the indexer was created from; null for older rows (treated as Generic Newznab). */
  definitionKey: string | null
  /** Appended to every API request, e.g. "&attrs=poster". */
  additionalParameters: string
  /** "yyyy-MM-dd" */
  vipExpiration: string | null
  /** Max API queries per unit; null or 0 = unlimited. */
  queryLimit: number | null
  /** Max NZB downloads per unit; null or 0 = unlimited. */
  grabLimit: number | null
  limitsUnit: LimitsUnit
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

/** Catalog entry for "Add indexer" (GET /indexer/definitions). */
export interface IndexerDefinition {
  key: string
  name: string
  implementation: string
  protocol: IndexerProtocol
  language: string
  privacy: IndexerPrivacy
  description: string
  siteUrl: string | null
  /** Empty for Generic Newznab. */
  baseUrl: string
  apiPath: string
  /** Top-level Newznab categories the site offers (2000, 5000, …). */
  categories: number[]
  supported: boolean
  unsupportedReason: string | null
}

export interface IndexerDetails {
  indexer: Indexer
  definition: IndexerDefinition | null
  capabilities: IndexerCapabilities | null
  capabilitiesCheckedAt: string | null
}

export interface IndexerTestResult {
  id: number
  name: string
  ok: boolean
  message: string | null
}

export interface ProwlarrConnection {
  url: string
  apiKey: string
}

export type ProwlarrImportMode = 'direct' | 'proxy'

export interface ProwlarrPreviewItem {
  prowlarrId: number
  name: string
  protocol: IndexerProtocol
  implementation: string
  baseUrl: string
  apiPath: string
  supported: boolean
  reason: string | null
  alreadyExists: boolean
  mode: ProwlarrImportMode
  /** The indexer's own address (base URL + API path) when it can be used without Prowlarr given its own API key. */
  ownUrl: string | null
}

export interface ProwlarrImportResult {
  imported: { id: number; name: string; mode: ProwlarrImportMode }[]
  skipped: { prowlarrId: number; name: string; reason: string }[]
}

export const maskedApiKey = '********'
export const genericDefinitionKey = 'generic-newznab'

/** Fills fields a server older than 0.4.1 does not send yet, so the form always has defined values. */
export function normalizeIndexer(i: Indexer): Indexer {
  return {
    ...i,
    enable: i.enable ?? true,
    definitionKey: i.definitionKey ?? null,
    additionalParameters: i.additionalParameters ?? '',
    vipExpiration: i.vipExpiration ?? null,
    queryLimit: i.queryLimit ?? null,
    grabLimit: i.grabLimit ?? null,
    limitsUnit: i.limitsUnit ?? 'day',
    status: i.status ?? null,
  }
}

export const indexersApi = {
  list: () => request<Indexer[]>('GET', '/indexer').then((list) => list.map(normalizeIndexer)),
  schema: () => request<Indexer>('GET', '/indexer/schema').then(normalizeIndexer),
  create: (indexer: Indexer) => request<Indexer>('POST', '/indexer', indexer),
  update: (indexer: Indexer) => request<Indexer>('PUT', `/indexer/${indexer.id}`, indexer),
  remove: (id: number) => request<void>('DELETE', `/indexer/${id}`),
  test: (indexer: Indexer) => request<IndexerCapabilities>('POST', '/indexer/test', indexer),
  definitions: () => request<IndexerDefinition[]>('GET', '/indexer/definitions'),
  details: (id: number) =>
    request<IndexerDetails>('GET', `/indexer/${id}/details`).then((d) => ({ ...d, indexer: normalizeIndexer(d.indexer) })),
  testAll: () => request<IndexerTestResult[]>('POST', '/indexer/testall'),
  prowlarrPreview: (connection: ProwlarrConnection) =>
    request<ProwlarrPreviewItem[]>('POST', '/indexer/import/prowlarr/preview', connection),
  prowlarrImport: (connection: ProwlarrConnection, ids: number[], keys: Record<number, string> = {}) =>
    request<ProwlarrImportResult>('POST', '/indexer/import/prowlarr', { ...connection, ids, keys }),
}

export const indexerKeys = {
  /** Prefix of every indexer query; invalidated on the hub message "indexersChanged". */
  all: ['indexers'] as const,
  list: ['indexers'] as const,
  details: (id: number) => ['indexers', 'details', id] as const,
  definitions: ['indexer-definitions'] as const,
}

/** Standard Newznab names, shown before the indexer's own list is loaded with "Test". */
export const standardCategoryNames: Record<number, string> = {
  1000: 'Console',
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
  3000: 'Audio',
  4000: 'PC',
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
  6000: 'XXX',
  7000: 'Books',
  8000: 'Other',
}

/** Newznab top-level categories, in catalog filter order. */
export const topCategories = [1000, 2000, 3000, 4000, 5000, 6000, 7000, 8000] as const
