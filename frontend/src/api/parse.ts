import { request } from './client'
import type { MatchedFormat } from './customFormats'

export interface ParsedRelease {
  kind: 'Movie' | 'Episode'
  title: string
  cleanTitle: string
  year: number
  seasonNumber: number | null
  episodeNumbers: number[]
  absoluteEpisodeNumbers: number[]
  airDate: string | null
  fullSeason: boolean
  multiSeason: boolean
  isSpecial: boolean
  quality: string
  version: number
  real: number
  isRepack: boolean
  languages: string[]
  releaseGroup: string | null
  edition: string | null
  hdr: string
  videoCodec: string | null
  audioCodec: string | null
  imdbId: string | null
  tmdbId: number | null
}

export interface DecisionResult {
  target: string
  profile: string
  accepted: boolean
  rejections: { code: string; message: string }[]
  preferredScore: number
  /** Custom formats the release matched (0.8) and their score in the target's profile. */
  customFormats?: MatchedFormat[]
  customFormatScore?: number
}

/** What one release profile says about the name. `limited`: it only applies to some indexers or quality profiles. */
export interface ReleaseProfileMatch {
  id: number
  name: string
  rejects: boolean
  missingRequired: boolean
  required: string[]
  requiredMatched: string[]
  ignoredMatched: string[]
  preferredMatched: { term: string; score: number }[]
  score: number
  limited: boolean
}

export interface ParseResult {
  parsed: ParsedRelease | null
  decision: DecisionResult | null
  releaseProfiles: ReleaseProfileMatch[]
  preferredScore: number
  hardcodedSubs: string | null
}

export function parseRelease(title: string, target: string, sizeMb: number | null, indexerId: number | null = null): Promise<ParseResult> {
  const params = new URLSearchParams({ title })
  const [kind, id] = target.split(':')
  if (kind === 'movie' && id) params.set('movieId', id)
  if (kind === 'series' && id) params.set('seriesId', id)
  if (sizeMb) params.set('sizeMb', String(sizeMb))
  if (indexerId) params.set('indexerId', String(indexerId))
  return request<ParseResult>('GET', `/parse?${params}`)
}
