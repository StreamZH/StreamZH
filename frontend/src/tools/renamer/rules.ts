import type { RenameRule, RenamerFilter, RenamerOptions, RenamerSource, RuleType, RunState } from '../../api/renamer'

/** A rule in the editor: the API rule plus a stable id for React keys and open/closed state. */
export type EditorRule = RenameRule & { id: string }

let nextId = 1
export const ruleId = () => `rule-${Date.now().toString(36)}-${nextId++}`

export const defaultSource: RenamerSource = {
  path: null,
  movieId: null,
  seriesId: null,
  subfolders: true,
  depth: 5,
  target: 'files',
}

export const defaultFilter: RenamerFilter = {
  extensions: 'video',
  customExtensions: '',
  include: '',
  exclude: '',
  minSizeMb: null,
  maxSizeMb: null,
  hideSamples: true,
}

export const defaultOptions: RenamerOptions = { folderFormat: '', companions: true }

/** New rules of each type, with `type` first (the API reads it first). */
export function newRule(type: RuleType): EditorRule {
  const id = ruleId()
  switch (type) {
    case 'replace':
      return { type, id, enabled: true, find: '', replaceWith: '', useRegex: false, caseSensitive: false, occurrence: 'all', scope: 'name' }
    case 'remove':
      return {
        type,
        id,
        enabled: true,
        mode: 'brackets',
        position: 1,
        count: 1,
        fromEnd: false,
        startMarker: '(',
        endMarker: ')',
        includeMarkers: true,
        square: true,
        round: true,
        curly: true,
        keepDecimalDots: true,
        scope: 'name',
      }
    case 'insert':
      return { type, id, enabled: true, text: '', where: 'prefix', position: 1, fromEnd: false, scope: 'name' }
    case 'case':
      return { type, id, enabled: true, mode: 'title', language: 'en', keepCapitals: true, scope: 'name' }
    case 'counter':
      return { type, id, enabled: true, start: 1, step: 1, padding: 2, where: 'prefix', position: 1, separator: ' - ', perFolder: false }
    case 'extension':
      return { type, id, enabled: true, mode: 'lower', newExtension: '' }
    case 'trim':
      return { type, id, enabled: true, characters: ' .-_', where: 'both', maxLength: null, scope: 'name' }
    case 'media':
      return {
        type,
        id,
        enabled: true,
        kind: 'auto',
        match: 'libraryAndTmdb',
        episodeFormat: '',
        movieFormat: '',
        season: null,
        episodeOffset: 0,
        searchTitle: '',
        tmdbId: null,
        useFolderName: true,
      }
  }
}

export const ruleTypes: { type: RuleType; label: string; hint: string }[] = [
  { type: 'media', label: 'Media names', hint: 'Series and movies: titles from the library or TMDB, built with your naming format.' },
  { type: 'replace', label: 'Replace', hint: 'Text or a regular expression with $1 groups.' },
  { type: 'remove', label: 'Remove', hint: 'Brackets, characters, text between markers, dots.' },
  { type: 'insert', label: 'Insert', hint: 'Prefix, suffix or text at a position, with tokens.' },
  { type: 'case', label: 'Case', hint: 'Title Case, Sentence case, lower, UPPER.' },
  { type: 'counter', label: 'Counter', hint: 'Numbers 01, 02 … per list or per folder.' },
  { type: 'extension', label: 'Extension', hint: 'Lower case, upper case or a new extension.' },
  { type: 'trim', label: 'Trim', hint: 'Spaces, dots and dashes at the ends; a maximum length.' },
]

export const ruleLabel = (type: RuleType) => ruleTypes.find((t) => t.type === type)?.label ?? type

const short = (text: string, max = 28) => (text.length > max ? `${text.slice(0, max - 1)}…` : text)

/** One line that says what the rule does, for its collapsed header. */
export function ruleSummary(rule: RenameRule): string {
  switch (rule.type) {
    case 'replace':
      return rule.find
        ? `${rule.useRegex ? '/' + short(rule.find) + '/' : `"${short(rule.find)}"`} → ${rule.replaceWith ? `"${short(rule.replaceWith)}"` : 'nothing'}`
        : 'Enter the text to find'
    case 'remove':
      switch (rule.mode) {
        case 'brackets':
          return [rule.square && '[…]', rule.round && '(…)', rule.curly && '{…}'].filter(Boolean).join(' ') + ' with content'
        case 'separators':
          return 'Dots and underscores to spaces'
        case 'spaces':
          return 'Double spaces'
        case 'between':
          return `Between ${rule.startMarker} and ${rule.endMarker}`
        default:
          return `${rule.count} character${rule.count === 1 ? '' : 's'} from ${rule.fromEnd ? 'the end' : 'position ' + rule.position}`
      }
    case 'insert':
      return rule.text ? `"${short(rule.text)}" ${rule.where === 'prefix' ? 'at the start' : rule.where === 'suffix' ? 'at the end' : `at ${rule.position}`}` : 'Enter the text'
    case 'case':
      return { title: 'Title Case', lower: 'lower case', upper: 'UPPER CASE', sentence: 'Sentence case' }[rule.mode]
    case 'counter':
      return `${String(rule.start).padStart(rule.padding, '0')}, ${String(rule.start + rule.step).padStart(rule.padding, '0')} … ${rule.where === 'replace' ? 'as the name' : rule.where}${rule.perFolder ? ', per folder' : ''}`
    case 'extension':
      return rule.mode === 'change' ? `To .${rule.newExtension.replace(/^\./, '') || '…'}` : rule.mode === 'remove' ? 'Remove' : rule.mode === 'upper' ? 'UPPER CASE' : 'lower case'
    case 'trim':
      return `${rule.characters ? `"${rule.characters}"` : ''}${rule.maxLength ? ` · max ${rule.maxLength}` : ''}`.trim() || 'Trim'
    case 'media': {
      const kind = rule.kind === 'episode' ? 'Episodes' : rule.kind === 'movie' ? 'Movies' : 'Series and movies'
      const match = { libraryAndTmdb: 'library, then TMDB', library: 'library', tmdb: 'TMDB', none: 'name only' }[rule.match]
      const extra = [rule.season != null && `season ${rule.season}`, rule.episodeOffset !== 0 && `offset ${rule.episodeOffset > 0 ? '+' : ''}${rule.episodeOffset}`]
        .filter(Boolean)
        .join(', ')
      return `${kind} · ${match}${extra ? ` · ${extra}` : ''}`
    }
  }
}

/** The rules as the API wants them: without the editor id, `type` first. */
export const toApiRules = (rules: EditorRule[]): RenameRule[] =>
  rules.map((rule) => {
    const { id: _id, type, ...rest } = rule
    return { type, ...rest } as RenameRule
  })

export const fromApiRules = (rules: RenameRule[]): EditorRule[] =>
  rules.map((rule) => ({ ...newRule(rule.type), ...rule, id: ruleId() }) as EditorRule)

/** Naming formats to pick from in the media rule (empty = Settings → Media Management). */
export const episodeFormats = [
  { label: 'Settings → Media Management', value: '' },
  { label: 'Plex, Jellyfin, Emby', value: '{Series Title} - S{season:00}E{episode:00} - {Episode Title}' },
  { label: 'With quality', value: '{Series Title} - S{season:00}E{episode:00} - {Episode Title} {Quality Full}' },
  { label: 'Scene (dots)', value: '{Series.Title}.S{season:00}E{episode:00}.{Episode.Title}.{Quality.Full}' },
  { label: 'Short (1x02)', value: '{Series Title} {season}x{episode:00} {Episode Title}' },
  { label: 'Only numbers', value: 'S{season:00}E{episode:00} - {Episode Title}' },
]

export const movieFormats = [
  { label: 'Settings → Media Management', value: '' },
  { label: 'Plex, Jellyfin, Emby', value: '{Movie Title} ({Release Year})' },
  { label: 'With quality', value: '{Movie Title} ({Release Year}) {Quality Full}' },
  { label: 'Plex with edition', value: '{Movie Title} ({Release Year}) {edition-{Edition Tags}}' },
  { label: 'Scene (dots)', value: '{Movie.Title}.{Release.Year}.{Quality.Full}-{Release Group}' },
]

export const folderFormats = [
  { label: 'Season 01', value: 'Season {season:00}' },
  { label: 'Season 1', value: 'Season {season}' },
  { label: 'S01', value: 'S{season:00}' },
  { label: 'Movie folder', value: '{Movie Title} ({Release Year})' },
]

/** Starting points when the list is empty. */
export const starters: { label: string; hint: string; rules: () => EditorRule[] }[] = [
  {
    label: 'Series episodes',
    hint: 'SxxEyy names with episode titles from TMDB',
    rules: () => [{ ...(newRule('media') as Extract<EditorRule, { type: 'media' }>), kind: 'episode' }],
  },
  {
    label: 'Movies',
    hint: 'Title (Year) from TMDB',
    rules: () => [{ ...(newRule('media') as Extract<EditorRule, { type: 'media' }>), kind: 'movie' }],
  },
  {
    label: 'Clean scene names',
    hint: 'Brackets out, dots to spaces, Title Case',
    rules: () => [
      newRule('remove'),
      { ...(newRule('remove') as Extract<EditorRule, { type: 'remove' }>), mode: 'separators' },
      { ...(newRule('remove') as Extract<EditorRule, { type: 'remove' }>), mode: 'spaces' },
      newRule('case'),
      newRule('trim'),
    ],
  },
  {
    label: 'Number the files',
    hint: '01 - name, 02 - name …',
    rules: () => [newRule('counter')],
  },
]

/* ---- simple mode ---- */

/** The simple renamer: one name pattern for episodes or movies, optionally season folders. */
export interface SimpleSetup {
  kind: 'episode' | 'movie'
  episodeFormat: string
  movieFormat: string
  seasonFolders: boolean
}

export const simpleEpisodeFormats = [
  { label: 'Title - S01E01 - Episode', value: '{Series Title} - S{season:00}E{episode:00} - {Episode Title}' },
  { label: 'Title - S01E01', value: '{Series Title} - S{season:00}E{episode:00}' },
  { label: 'With quality', value: '{Series Title} - S{season:00}E{episode:00} - {Episode Title} {Quality Full}' },
]

export const simpleMovieFormats = [
  { label: 'Title (Year)', value: '{Movie Title} ({Release Year})' },
  { label: 'With quality', value: '{Movie Title} ({Release Year}) {Quality Full}' },
]

export const defaultSimple: SimpleSetup = {
  kind: 'episode',
  episodeFormat: simpleEpisodeFormats[0].value,
  movieFormat: simpleMovieFormats[0].value,
  seasonFolders: true,
}

/** Runs that are still going (the history refreshes every second while one is). */
export const activeStates: RunState[] = ['queued', 'running', 'undoing']
