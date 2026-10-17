import { request } from './client'
import type { DownloadItem } from './downloads'
import type { MediaType } from './library'

export type ColonReplacement = 'delete' | 'dash' | 'spaceDash' | 'spaceDashSpace' | 'smart'
export type MultiEpisodeStyle = 'extend' | 'duplicate' | 'repeat' | 'scene' | 'range' | 'prefixedRange'
export type ImportMode = 'move' | 'copy'
export type EpisodeTitleRequired = 'always' | 'bulkSeasonReleases' | 'never'
export type ProperHandling = 'preferAndUpgrade' | 'doNotUpgrade' | 'doNotPrefer'
export type RescanAfterRefresh = 'always' | 'afterManualRefresh' | 'never'
export type FileDateMode = 'none' | 'releaseDate'

/** GET/PUT /settings/media-management. `isWindows` is read-only. */
export interface MediaManagementSettings {
  renameMovies: boolean
  replaceIllegalCharacters: boolean
  colonReplacement: ColonReplacement
  standardMovieFormat: string
  movieFolderFormat: string
  renameEpisodes: boolean
  standardEpisodeFormat: string
  dailyEpisodeFormat: string
  animeEpisodeFormat: string
  seriesFolderFormat: string
  useSeasonFolders: boolean
  seasonFolderFormat: string
  specialsFolderFormat: string
  multiEpisodeStyle: MultiEpisodeStyle
  createEmptyFolders: boolean
  deleteEmptyFolders: boolean
  skipFreeSpaceCheck: boolean
  minimumFreeSpaceMb: number
  importMode: ImportMode
  copyUsingHardlinks: boolean
  importExtraFiles: boolean
  extraFileExtensions: string
  episodeTitleRequired: EpisodeTitleRequired
  propersAndRepacks: ProperHandling
  rescanAfterRefresh: RescanAfterRefresh
  changeFileDate: FileDateMode
  recycleBin: string
  recycleBinCleanupDays: number
  unmonitorDeletedFiles: boolean
  setPermissions: boolean
  chmodFolder: string
  chownGroup: string
  completedDownloadHandling: boolean
  removeCompletedDownloads: boolean
  redownloadFailed: boolean
  removeFailedDownloads: boolean
  isWindows: boolean
}

/** Example names for the formats being edited; a format with an error gives '' and an entry in `errors`. */
export interface NamingPreview {
  movieFile: string
  movieFolder: string
  standardEpisode: string
  multiEpisode: string
  dailyEpisode: string
  animeEpisode: string
  seriesFolder: string
  seasonFolder: string | null
  specialsFolder: string | null
  errors: Record<string, string[]>
}

export type ImportStateName = 'queued' | 'importing' | 'imported' | 'failed' | 'waiting'

/** Where the import of a finished download stands. */
export interface ImportState {
  downloadId: number
  state: ImportStateName
  message: string | null
  date: string
  title: string
  mediaType: MediaType | null
  movieId: number | null
  seriesId: number | null
  episodeIds: number[]
  files: string[]
  isUpgrade: boolean
  manual: boolean
  dismissed: boolean
}

export interface PendingImport {
  download: DownloadItem
  import: ImportState
}

/** A video file of a download in the manual import dialog. */
export interface ImportFileCandidate {
  relativePath: string
  size: number
  quality: string
  languages: string[]
  seasonNumber: number | null
  episodeNumbers: number[]
  episodeIds: number[]
  problem: string | null
}

export interface ManualImportRequest {
  mediaType: MediaType
  movieId?: number
  seriesId?: number
  files: { path: string; episodeIds: number[] }[]
}

export interface BlocklistItem {
  id: number
  date: string
  mediaType: MediaType | null
  movieId: number | null
  seriesId: number | null
  episodeIds: number[]
  sourceTitle: string
  quality: string
  languages: string[]
  indexer: string | null
  size: number
  message: string | null
}

export interface BlocklistPage {
  page: number
  pageSize: number
  totalRecords: number
  records: BlocklistItem[]
}

/** A file whose name does not follow the naming scheme (paths relative to the movie or series folder). */
export interface RenamePreview {
  episodeIds: number[]
  seasonNumber: number | null
  existingPath: string
  newPath: string
}

export const mediaManagementKeys = {
  settings: ['settings', 'media-management'] as const,
  preview: (values: unknown) => ['settings', 'media-management', 'preview', values] as const,
  imports: (ids: number[]) => ['imports', ids] as const,
  pending: ['imports', 'pending'] as const,
  allImports: ['imports'] as const,
  files: (downloadId: number, seriesId: number | null) => ['imports', 'files', downloadId, seriesId] as const,
  blocklist: (page: number, pageSize: number) => ['blocklist', page, pageSize] as const,
  allBlocklist: ['blocklist'] as const,
  rename: (type: MediaType, id: number, season?: number) => ['rename', type, id, season ?? null] as const,
}

export const mediaManagementApi = {
  settings: () => request<MediaManagementSettings>('GET', '/settings/media-management'),
  save: (values: MediaManagementSettings) => request<MediaManagementSettings>('PUT', '/settings/media-management', values),
  preview: (values: MediaManagementSettings) => request<NamingPreview>('POST', '/settings/media-management/preview', values),

  imports: (ids: number[]) => request<ImportState[]>('GET', `/import/downloads?ids=${ids.join(',')}`),
  pending: () => request<PendingImport[]>('GET', '/import/pending'),
  importNow: (downloadId: number) => request<unknown>('POST', `/import/downloads/${downloadId}`),
  files: (downloadId: number, seriesId: number | null) =>
    request<ImportFileCandidate[]>('GET', `/import/downloads/${downloadId}/files${seriesId != null ? `?seriesId=${seriesId}` : ''}`),
  manualImport: (downloadId: number, body: ManualImportRequest) => request<unknown>('POST', `/import/downloads/${downloadId}/manual`, body),
  dismiss: (downloadId: number) => request<void>('POST', `/import/downloads/${downloadId}/dismiss`),

  blocklist: (page: number, pageSize: number) => request<BlocklistPage>('GET', `/blocklist?page=${page}&pageSize=${pageSize}`),
  unblock: (id: number) => request<void>('DELETE', `/blocklist/${id}`),
  clearBlocklist: () => request<{ removed: number }>('DELETE', '/blocklist'),

  renamePreview: (type: MediaType, id: number, season?: number) =>
    request<RenamePreview[]>('GET', type === 'Movie' ? `/rename/movie/${id}` : `/rename/series/${id}${season != null ? `?season=${season}` : ''}`),
  rename: (type: MediaType, id: number, season?: number, files?: string[]) =>
    request<{ renamed: number }>('POST', type === 'Movie' ? `/rename/movie/${id}` : `/rename/series/${id}`, type === 'Movie' ? undefined : { season, files }),
}
