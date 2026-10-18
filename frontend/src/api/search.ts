import { request } from './client'
import type { MatchedFormat } from './customFormats'
import type { GrabResult } from './releases'

/** Search page: free text search over the indexers, without a library item (contract §7). */
export type SearchType = 'search' | 'movie' | 'tv'

export interface SearchParams {
  query: string
  type: SearchType
  categories: number[]
  indexerIds: number[]
}

export interface FreeRelease {
  guid: string
  indexerId: number
  indexer: string
  title: string
  size: number
  ageHours: number
  publishDate: string
  grabs: number | null
  categories: number[]
  infoUrl: string | null
  /** Read from the release name, display only. */
  quality: string
  languages: string[]
  /** Custom formats the name matches (0.8, when the server sends them; no profile, so the score is display only). */
  customFormats?: MatchedFormat[]
  customFormatScore?: number
}

export interface FreeSearch {
  /** Indexers asked; 0 means none is enabled for interactive search (or none of the chosen ones). */
  indexers: number
  failedIndexers: { indexerId: number; indexer: string; message: string }[]
  releases: FreeRelease[]
}

export const searchKeys = {
  search: (params: SearchParams) => ['free-search', params] as const,
}

export const searchApi = {
  search: (params: SearchParams) => {
    const query = new URLSearchParams({ query: params.query, type: params.type })
    if (params.categories.length > 0) query.set('categories', params.categories.join(','))
    if (params.indexerIds.length > 0) query.set('indexerIds', params.indexerIds.join(','))
    return request<FreeSearch>('GET', `/search?${query}`)
  },
  /** `category`: the download category for the built-in engine (default: other). */
  grab: (indexerId: number, guid: string, category?: string) => request<GrabResult>('POST', '/search/grab', { indexerId, guid, category }),
}

export interface CategoryGroup {
  id: number
  name: string
  subs: { id: number; name: string }[]
}

/** Newznab top categories with their common sub-categories (the standard numbering every indexer uses). */
export const newznabCategories: CategoryGroup[] = [
  {
    id: 2000,
    name: 'Movies',
    subs: [
      { id: 2010, name: 'Foreign' },
      { id: 2030, name: 'SD' },
      { id: 2040, name: 'HD' },
      { id: 2045, name: 'UHD' },
      { id: 2050, name: 'BluRay' },
      { id: 2060, name: '3D' },
      { id: 2070, name: 'DVD' },
      { id: 2080, name: 'WEB-DL' },
      { id: 2090, name: 'x265' },
      { id: 2020, name: 'Other' },
    ],
  },
  {
    id: 5000,
    name: 'TV',
    subs: [
      { id: 5010, name: 'WEB-DL' },
      { id: 5020, name: 'Foreign' },
      { id: 5030, name: 'SD' },
      { id: 5040, name: 'HD' },
      { id: 5045, name: 'UHD' },
      { id: 5060, name: 'Sport' },
      { id: 5070, name: 'Anime' },
      { id: 5080, name: 'Documentary' },
      { id: 5090, name: 'x265' },
      { id: 5050, name: 'Other' },
    ],
  },
  {
    id: 3000,
    name: 'Audio',
    subs: [
      { id: 3010, name: 'MP3' },
      { id: 3040, name: 'Lossless' },
      { id: 3030, name: 'Audiobook' },
      { id: 3020, name: 'Video' },
      { id: 3050, name: 'Other' },
    ],
  },
  {
    id: 7000,
    name: 'Books',
    subs: [
      { id: 7020, name: 'EBook' },
      { id: 7030, name: 'Comics' },
      { id: 7010, name: 'Mags' },
      { id: 7040, name: 'Technical' },
      { id: 7060, name: 'Foreign' },
    ],
  },
  {
    id: 4000,
    name: 'PC',
    subs: [
      { id: 4050, name: 'Games' },
      { id: 4030, name: 'Mac' },
      { id: 4070, name: 'Mobile-Android' },
      { id: 4060, name: 'Mobile-iOS' },
    ],
  },
  { id: 1000, name: 'Console', subs: [] },
  { id: 6000, name: 'XXX', subs: [] },
  { id: 8000, name: 'Other', subs: [{ id: 8010, name: 'Misc' }] },
]

const categoryNames = new Map<number, string>(
  newznabCategories.flatMap((group) => [
    [group.id, group.name] as [number, string],
    ...group.subs.map((sub) => [sub.id, `${group.name}/${sub.name}`] as [number, string]),
  ]),
)

/** "Movies/UHD" for 2045; indexer-specific numbers (100000+) and unknown ones fall back to their top category. */
export function categoryName(id: number): string {
  const known = categoryNames.get(id)
  if (known) return known
  if (id >= 100000) return `Custom ${id}`
  const top = categoryNames.get(Math.floor(id / 1000) * 1000)
  return top ? `${top}/${id}` : String(id)
}

/** Age as "35 min", "5 h", "3 days", "2.1 years". */
export function formatAgeHours(hours: number): string {
  if (hours < 1) return `${Math.max(1, Math.round(hours * 60))} min`
  if (hours < 24) return `${Math.round(hours)} h`
  const days = Math.floor(hours / 24)
  if (days < 365) return days === 1 ? '1 day' : `${days} days`
  return `${(days / 365).toFixed(1)} years`
}
