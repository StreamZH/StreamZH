import { request } from './client'

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
}

export interface ParseResult {
  parsed: ParsedRelease | null
  decision: DecisionResult | null
}

export function parseRelease(title: string, target: string, sizeMb: number | null): Promise<ParseResult> {
  const params = new URLSearchParams({ title })
  const [kind, id] = target.split(':')
  if (kind === 'movie' && id) params.set('movieId', id)
  if (kind === 'series' && id) params.set('seriesId', id)
  if (sizeMb) params.set('sizeMb', String(sizeMb))
  return request<ParseResult>('GET', `/parse?${params}`)
}
