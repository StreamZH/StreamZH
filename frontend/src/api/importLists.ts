import { request } from './client'
import type { ConnectField, SettingValue } from './connect'
import type { MediaType } from './library'

export type ImportListGroup = 'tmdb' | 'trakt' | 'plex' | 'other'
export type CleanLibraryLevel = 'disabled' | 'logOnly' | 'keepAndUnmonitor' | 'removeAndKeepFiles'
export type PreviewState = 'new' | 'waiting' | 'inLibrary' | 'excluded' | 'notFound'

export interface Option {
  value: string
  label: string
}

/** A field of a list type: the Connect field plus the media type and a condition on another field. */
export interface ImportListField extends ConnectField {
  /** Only for lists of this media type; null = both. */
  media: 'movie' | 'series' | null
  /** Only shown while another field has one of these values. */
  when: { field: string; values: string[] } | null
}

export interface ImportListType {
  key: string
  name: string
  group: ImportListGroup
  description: string
  infoUrl: string | null
  /** What a list of this type can add. */
  mediaTypes: MediaType[]
  fields: ImportListField[]
}

export interface ImportListSchema {
  types: ImportListType[]
  movieMonitorOptions: Option[]
  seriesMonitorOptions: Option[]
  availabilityOptions: Option[]
}

export interface ImportListStatus {
  /** The last sync failed. */
  failing: boolean
  lastSync: string | null
  lastSuccess: string | null
  lastError: string | null
  /** Failed syncs in a row. */
  failures: number
  lastFound: number | null
  lastAdded: number | null
  lastAddedTitles: string[]
}

export interface ImportList {
  id: number
  name: string
  implementation: string
  implementationName: string
  mediaType: MediaType
  enabled: boolean
  /** Adds new items on its own; off: only the preview shows them. */
  enableAuto: boolean
  searchOnAdd: boolean
  monitor: string
  rootFolderId: number
  /** Null = the default profile. */
  qualityProfileId: number | null
  minimumAvailability: string
  /** Every field of the type; secrets come as "********" and keep the stored value when sent back. */
  settings: Record<string, SettingValue>
  status: ImportListStatus
}

export interface PreviewItem {
  state: PreviewState
  mediaType: MediaType
  tmdbId: number | null
  title: string
  year: number
  imdbId: string | null
  /** Waiting items: from when (yyyy-MM-dd). */
  availableFrom: string | null
}

export interface ImportListOptions {
  /** Hours between two syncs of all lists. */
  syncInterval: number
  cleanLibraryLevel: CleanLibraryLevel
}

export interface ImportListExclusion {
  id: number
  mediaType: MediaType
  tmdbId: number
  title: string
  year: number
  added: string
}

export const importListKeys = {
  all: ['import-lists'] as const,
  list: ['import-lists', 'list'] as const,
  schema: ['import-lists', 'schema'] as const,
  options: ['import-lists', 'options'] as const,
  exclusions: ['import-lists', 'exclusions'] as const,
  origin: (mediaType: MediaType, tmdbId: number) => ['import-lists', 'origin', mediaType, tmdbId] as const,
}

export const monitorDefault = (mediaType: MediaType) => (mediaType === 'Series' ? 'all' : 'movieOnly')

/** Field default, or the empty value of its type. */
export function fieldDefault(f: ConnectField): SettingValue {
  return f.default ?? (f.type === 'bool' ? false : f.type === 'multiSelect' ? [] : f.type === 'number' ? null : '')
}

/** A new list of this type with the field defaults. */
export function newImportList(type: ImportListType, rootFolderId: number, name: string): ImportList {
  const mediaType = type.mediaTypes[0]
  return {
    id: 0,
    name,
    implementation: type.key,
    implementationName: type.name,
    mediaType,
    enabled: true,
    enableAuto: true,
    searchOnAdd: true,
    monitor: monitorDefault(mediaType),
    rootFolderId,
    qualityProfileId: null,
    minimumAvailability: 'announced',
    settings: Object.fromEntries(type.fields.map((f) => [f.name, fieldDefault(f)])),
    status: { failing: false, lastSync: null, lastSuccess: null, lastError: null, failures: 0, lastFound: null, lastAdded: null, lastAddedTitles: [] },
  }
}

/** The field applies to this media type and its condition holds. */
export function fieldApplies(f: ImportListField, list: ImportList): boolean {
  if (f.media && f.media !== (list.mediaType === 'Series' ? 'series' : 'movie')) return false
  if (!f.when) return true
  const value = list.settings[f.when.field]
  return f.when.values.includes(typeof value === 'string' ? value : '')
}

const body = (l: ImportList) => {
  const { status: _status, implementationName: _name, ...rest } = l
  return rest
}

export const importListApi = {
  list: () => request<ImportList[]>('GET', '/importlist'),
  schema: () => request<ImportListSchema>('GET', '/importlist/schema'),
  create: (l: ImportList) => request<ImportList>('POST', '/importlist', body(l)),
  update: (l: ImportList) => request<ImportList>('PUT', `/importlist/${l.id}`, body(l)),
  remove: (id: number) => request<void>('DELETE', `/importlist/${id}`),
  /** Nothing is saved; with the id of a saved list, masked secrets use the stored ones. */
  test: (l: ImportList) => request<{ message: string }>('POST', '/importlist/test', body(l)),
  preview: (l: ImportList) => request<{ name: string | null; items: PreviewItem[] }>('POST', '/importlist/preview', body(l)),
  /** Adds one item of a saved list now, with the list's options. */
  add: (id: number, tmdbId: number) => request<{ added: string[] }>('POST', `/importlist/${id}/add`, { tmdbId }),
  sync: (id: number) => request<{ queued: boolean }>('POST', `/importlist/${id}/sync`),
  syncAll: () => request<{ queued: boolean }>('POST', '/importlist/sync'),
  origin: (mediaType: MediaType, tmdbId: number) =>
    request<{ listId: number | null; listName: string | null }>('GET', `/importlist/origin?mediaType=${mediaType}&tmdbId=${tmdbId}`),

  options: () => request<ImportListOptions>('GET', '/settings/importlists'),
  saveOptions: (o: ImportListOptions) => request<ImportListOptions>('PUT', '/settings/importlists', o),

  exclusions: () => request<ImportListExclusion[]>('GET', '/importlist/exclusion'),
  addExclusion: (mediaType: MediaType, tmdbId: number, title: string, year: number) =>
    request<ImportListExclusion>('POST', '/importlist/exclusion', { mediaType, tmdbId, title, year }),
  removeExclusion: (id: number) => request<void>('DELETE', `/importlist/exclusion/${id}`),
}
