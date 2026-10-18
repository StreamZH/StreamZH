import { request } from './client'

// Tools → File renamer (/api/v1/renamer). Rules are a tagged union on `type`.

export type RenameTarget = 'files' | 'folders' | 'filesAndFolders'
export type ExtensionPreset = 'video' | 'videoAndSubtitles' | 'subtitles' | 'images' | 'all' | 'custom'
export type RuleScope = 'name' | 'fullName'

export interface RenamerSource {
  path: string | null
  movieId: number | null
  seriesId: number | null
  subfolders: boolean
  depth: number
  target: RenameTarget
}

export interface RenamerFilter {
  extensions: ExtensionPreset
  customExtensions: string
  include: string
  exclude: string
  minSizeMb: number | null
  maxSizeMb: number | null
  hideSamples: boolean
}

interface RuleBase {
  enabled: boolean
}

export interface ReplaceRule extends RuleBase {
  type: 'replace'
  find: string
  replaceWith: string
  useRegex: boolean
  caseSensitive: boolean
  occurrence: 'all' | 'first' | 'last'
  scope: RuleScope
}

export type RemoveMode = 'characters' | 'between' | 'brackets' | 'separators' | 'spaces'

export interface RemoveRule extends RuleBase {
  type: 'remove'
  mode: RemoveMode
  position: number
  count: number
  fromEnd: boolean
  startMarker: string
  endMarker: string
  includeMarkers: boolean
  square: boolean
  round: boolean
  curly: boolean
  keepDecimalDots: boolean
  scope: RuleScope
}

export interface InsertRule extends RuleBase {
  type: 'insert'
  text: string
  where: 'prefix' | 'suffix' | 'position'
  position: number
  fromEnd: boolean
  scope: RuleScope
}

export type CaseMode = 'title' | 'lower' | 'upper' | 'sentence'

export interface CaseRule extends RuleBase {
  type: 'case'
  mode: CaseMode
  language: string
  keepCapitals: boolean
  scope: RuleScope
}

export interface CounterRule extends RuleBase {
  type: 'counter'
  start: number
  step: number
  padding: number
  where: 'prefix' | 'suffix' | 'position' | 'replace'
  position: number
  separator: string
  perFolder: boolean
}

export interface ExtensionRule extends RuleBase {
  type: 'extension'
  mode: 'lower' | 'upper' | 'change' | 'remove'
  newExtension: string
}

export interface TrimRule extends RuleBase {
  type: 'trim'
  characters: string
  where: 'both' | 'start' | 'end'
  maxLength: number | null
  scope: RuleScope
}

export type MediaKind = 'auto' | 'episode' | 'movie'
export type MediaMatch = 'libraryAndTmdb' | 'library' | 'tmdb' | 'none'

export interface MediaRule extends RuleBase {
  type: 'media'
  kind: MediaKind
  match: MediaMatch
  episodeFormat: string
  movieFormat: string
  season: number | null
  episodeOffset: number
  searchTitle: string
  tmdbId: number | null
  useFolderName: boolean
}

export type RenameRule = ReplaceRule | RemoveRule | InsertRule | CaseRule | CounterRule | ExtensionRule | TrimRule | MediaRule
export type RuleType = RenameRule['type']

export interface RenamerOptions {
  folderFormat: string
  companions: boolean
}

export interface RenamerRequest {
  source: RenamerSource
  filter: RenamerFilter
  rules: RenameRule[]
  options: RenamerOptions
  excluded: string[]
}

export interface ScanEntry {
  key: string
  folder: string
  name: string
  isFolder: boolean
  size: number
  modified: string
}

export interface ScanResult {
  folder: string
  total: number
  matched: number
  entries: ScanEntry[]
  truncated: boolean
  movieId: number | null
  seriesId: number | null
  itemTitle: string | null
}

export type RowStatus = 'unchanged' | 'rename' | 'conflict' | 'error' | 'excluded'

export interface MediaMatchInfo {
  kind: 'episode' | 'movie'
  title: string
  year: number
  season: number | null
  episodes: number[]
  episodeTitle: string | null
  source: 'library' | 'tmdb' | 'name'
  note: string | null
}

export interface PreviewRow {
  key: string
  folder: string
  oldName: string
  newName: string
  newFolder: string
  isFolder: boolean
  size: number
  status: RowStatus
  problem: string | null
  message: string | null
  companionOf: string | null
  media: MediaMatchInfo | null
  movieId: number | null
  seriesId: number | null
}

export interface PreviewCounts {
  total: number
  unchanged: number
  rename: number
  conflict: number
  error: number
  excluded: number
}

export interface PreviewResult {
  folder: string
  total: number
  matched: number
  truncated: boolean
  rows: PreviewRow[]
  counts: PreviewCounts
  /** By rule index ("0", "1" …). */
  ruleErrors: Record<string, string>
  folderFormatError: string | null
}

export type RunState = 'queued' | 'running' | 'completed' | 'partial' | 'failed' | 'undoing' | 'undone'

export interface RenamerRun {
  id: number
  started: string
  finished: string | null
  folder: string
  state: RunState
  total: number
  done: number
  failed: number
  error: string | null
  summary: string
  undoneAt: string | null
  rules: RenameRule[]
}

export interface RenamerRunItem {
  order: number
  /** removeFolder: a folder the run left empty and removed (oldPath); undo creates it again. */
  kind: 'rename' | 'createFolder' | 'removeFolder'
  oldPath: string
  newPath: string
  isFolder: boolean
  state: 'planned' | 'done' | 'failed' | 'skipped' | 'undone'
  error: string | null
}

export interface RenamerRunDetail {
  run: RenamerRun
  items: RenamerRunItem[]
}

export interface RenamerPresetContent {
  filter: RenamerFilter
  rules: RenameRule[]
  options: RenamerOptions
}

export interface RenamerPreset {
  id: number
  name: string
  content: RenamerPresetContent
  updated: string
}

export interface RegexTestGroup {
  number: number
  name: string
  value: string | null
  index: number | null
}

export interface RegexTestResult {
  error: string | null
  matches: { index: number; length: number; value: string; groups: RegexTestGroup[] }[]
  result: string | null
}

export const renamerApi = {
  scan: (source: RenamerSource, filter: RenamerFilter) => request<ScanResult>('POST', '/renamer/scan', { source, filter }),
  preview: (body: RenamerRequest) => request<PreviewResult>('POST', '/renamer/preview', body),
  apply: (body: RenamerRequest) => request<{ runId: number }>('POST', '/renamer/apply', body),
  history: (limit = 50) => request<RenamerRun[]>('GET', `/renamer/history?limit=${limit}`),
  run: (id: number) => request<RenamerRunDetail>('GET', `/renamer/history/${id}`),
  undo: (id: number) => request<{ runId: number }>('POST', `/renamer/undo/${id}`),
  presets: () => request<RenamerPreset[]>('GET', '/renamer/presets'),
  addPreset: (name: string, content: RenamerPresetContent) => request<RenamerPreset>('POST', '/renamer/presets', { name, content }),
  updatePreset: (id: number, name: string, content: RenamerPresetContent) => request<RenamerPreset>('PUT', `/renamer/presets/${id}`, { name, content }),
  deletePreset: (id: number) => request<void>('DELETE', `/renamer/presets/${id}`),
  testRegex: (pattern: string, caseSensitive: boolean, input: string, replacement: string | null) =>
    request<RegexTestResult>('POST', '/renamer/regex-test', { pattern, caseSensitive, input, replacement }),
}

export const renamerKeys = {
  all: ['renamer'] as const,
  scan: (source: unknown, filter: unknown) => ['renamer', 'scan', source, filter] as const,
  preview: (body: unknown) => ['renamer', 'preview', body] as const,
  history: ['renamer', 'history'] as const,
  run: (id: number) => ['renamer', 'run', id] as const,
  presets: ['renamer', 'presets'] as const,
  regex: (pattern: string, caseSensitive: boolean, input: string, replacement: string | null) =>
    ['renamer', 'regex', pattern, caseSensitive, input, replacement] as const,
}
