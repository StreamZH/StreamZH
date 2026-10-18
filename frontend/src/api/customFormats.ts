import { request } from './client'

/** Custom formats like Radarr/Sonarr: named sets of conditions on a release; quality profiles give them a score. */
export type FormatMediaType = 'movie' | 'series'

export type FieldValue = string | number

export interface FormatSpecification {
  name: string
  /** Radarr/Sonarr implementation name, e.g. "ReleaseTitleSpecification". */
  implementation: string
  negate: boolean
  required: boolean
  fields: { value?: FieldValue; min?: number; max?: number }
}

export interface CustomFormat {
  id: number
  name: string
  mediaType: FormatMediaType
  includeInRename: boolean
  specifications: FormatSpecification[]
  /** TRaSH Guides id when the format comes from the guides; null for own formats. */
  trashId: string | null
  /** Profiles that give this format a non-zero score. */
  usedBy: number
}

export type SchemaFieldType = 'text' | 'regex' | 'number' | 'select' | 'flags'

export interface SchemaField {
  name: 'value' | 'min' | 'max'
  label: string
  type: SchemaFieldType
  unit?: string
  options?: { value: number; label: string }[]
}

export interface FormatSchema {
  implementation: string
  label: string
  description: string
  fields: SchemaField[]
}

export interface FormatTestRequest {
  title: string
  mediaType: FormatMediaType
  sizeGb?: number
  qualityProfileId?: number
}

export interface FormatTestResult {
  parsed: Record<string, unknown> | null
  formats: {
    id: number
    name: string
    matched: boolean
    score?: number | null
    specifications: { name: string; implementation: string; matched: boolean }[]
  }[]
  totalScore: number
}

/** The formats a release matched and their summed score, as release lists carry them. */
export interface MatchedFormat {
  id: number
  name: string
}

export const customFormatKeys = {
  all: ['custom-formats'] as const,
  schema: (mediaType: FormatMediaType) => ['custom-format-schema', mediaType] as const,
}

export const customFormatsApi = {
  list: () => request<CustomFormat[]>('GET', '/customformat'),
  get: (id: number) => request<CustomFormat>('GET', `/customformat/${id}`),
  create: (format: CustomFormat) => request<CustomFormat>('POST', '/customformat', format),
  update: (format: CustomFormat) => request<CustomFormat>('PUT', `/customformat/${format.id}`, format),
  remove: (id: number) => request<void>('DELETE', `/customformat/${id}`),
  schema: (mediaType: FormatMediaType) => request<FormatSchema[]>('GET', `/customformat/schema?mediaType=${mediaType}`),
  /** Radarr/Sonarr export or TRaSH JSON, one object or an array. */
  import: (mediaType: FormatMediaType, json: string) => request<CustomFormat[]>('POST', '/customformat/import', { mediaType, json }),
  export: (id: number) => request<unknown>('GET', `/customformat/${id}/export`),
  test: (body: FormatTestRequest) => request<FormatTestResult>('POST', '/customformat/test', body),
}

export const implementationShort: Record<string, string> = {
  ReleaseTitleSpecification: 'Release title',
  ReleaseGroupSpecification: 'Release group',
  EditionSpecification: 'Edition',
  LanguageSpecification: 'Language',
  SourceSpecification: 'Source',
  ResolutionSpecification: 'Resolution',
  QualityModifierSpecification: 'Quality modifier',
  SizeSpecification: 'Size',
  IndexerFlagSpecification: 'Indexer flag',
  ReleaseTypeSpecification: 'Release type',
  YearSpecification: 'Year',
}

export function implementationLabel(implementation: string): string {
  return implementationShort[implementation] ?? implementation.replace(/Specification$/, '')
}

/** "+1,500", "−10,000", "0". */
export function formatFormatScore(score: number): string {
  if (score === 0) return '0'
  const text = Math.abs(score).toLocaleString('en-US')
  return score > 0 ? `+${text}` : `−${text}`
}
