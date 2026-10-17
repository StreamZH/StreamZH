import { request } from './client'

export type MediaType = 'Movie' | 'Series'

export interface MediaFile {
  relativePath: string
  size: number
  modified: string
}

export interface Movie {
  id: number
  tmdbId: number
  imdbId: string | null
  title: string
  originalTitle: string
  year: number
  overview: string | null
  runtime: number
  status: string
  genres: string[]
  posterPath: string | null
  backdropPath: string | null
  monitored: boolean
  qualityProfileId: number
  rootFolderId: number
  folderPath: string
  added: string
  lastRefreshed: string | null
  hasFile: boolean
  file: MediaFile | null
}

export interface Series {
  id: number
  tmdbId: number
  tvdbId: number | null
  imdbId: string | null
  title: string
  originalTitle: string
  year: number
  overview: string | null
  status: string
  network: string | null
  genres: string[]
  posterPath: string | null
  backdropPath: string | null
  monitored: boolean
  qualityProfileId: number
  rootFolderId: number
  folderPath: string
  added: string
  lastRefreshed: string | null
  episodeCount: number
  airedCount: number
  fileCount: number
  sizeOnDisk: number
}

export interface Episode {
  id: number
  seasonNumber: number
  episodeNumber: number
  title: string
  airDate: string | null
  overview: string | null
  monitored: boolean
  hasFile: boolean
  file: MediaFile | null
}

export interface Season {
  seasonNumber: number
  episodeCount: number
  fileCount: number
  monitored: boolean
}

export interface SeriesDetail {
  series: Series
  seasons: Season[]
  episodes: Episode[]
}

export interface LookupResult {
  tmdbId: number
  title: string
  originalTitle: string
  year: number
  overview: string | null
  posterPath: string | null
  libraryId: number | null
}

export interface RootFolder {
  id: number
  path: string
  mediaType: MediaType
  accessible: boolean
  writable: boolean
  writeProblem: string | null
  freeSpace: number | null
  totalSpace: number | null
  unmappedFolders: number
}

export interface FolderEntry {
  name: string
  path: string
  hint: string | null
}

export interface FolderListing {
  path: string | null
  parent: string | null
  folders: FolderEntry[]
}

export interface ImportCandidate {
  folderName: string
  path: string
  title: string
  year: number | null
  tmdbId: number | null
  videoFiles: number
  size: number
}

export interface LibraryStats {
  movies: number
  moviesWithFile: number
  series: number
  episodes: number
  episodesWithFile: number
  sizeOnDisk: number
}

export interface JobStatus {
  queued: number
  running: string | null
  completedSinceStart: number
  failedSinceStart: number
}

export interface MetadataSettings {
  tmdbConfigured: boolean
  tmdbTokenHint: string | null
  language: string
  languages: string[]
}

const path = (type: MediaType) => (type === 'Movie' ? 'movies' : 'series')

export const libraryApi = {
  movies: () => request<Movie[]>('GET', '/movies'),
  movie: (id: number) => request<Movie>('GET', `/movies/${id}`),
  series: () => request<Series[]>('GET', '/series'),
  seriesDetail: (id: number) => request<SeriesDetail>('GET', `/series/${id}`),
  lookup: (type: MediaType, term: string, year?: number | null) =>
    request<LookupResult[]>('GET', `/lookup/${path(type)}?term=${encodeURIComponent(term)}${year ? `&year=${year}` : ''}`),
  /** `searchForMissing` null: the default from Settings → Indexers. */
  add: (type: MediaType, tmdbId: number, rootFolderId: number, monitored: boolean, qualityProfileId: number | null, searchForMissing: boolean | null = null) =>
    request<Movie | Series>('POST', `/${path(type)}`, { tmdbId, rootFolderId, monitored, qualityProfileId, searchForMissing }),
  setMonitored: (type: MediaType, id: number, monitored: boolean) =>
    request<void>('PUT', `/${path(type)}/${id}/monitor`, { monitored }),
  setSeasonMonitored: (id: number, season: number, monitored: boolean) =>
    request<void>('PUT', `/series/${id}/seasons/${season}/monitor`, { monitored }),
  refresh: (type: MediaType, id: number) => request<JobStatus>('POST', `/${path(type)}/${id}/refresh`),
  remove: (type: MediaType, id: number) => request<void>('DELETE', `/${path(type)}/${id}`),

  rootFolders: () => request<RootFolder[]>('GET', '/rootfolders'),
  addRootFolder: (folderPath: string, mediaType: MediaType) =>
    request<RootFolder>('POST', '/rootfolders', { path: folderPath, mediaType }),
  removeRootFolder: (id: number) => request<void>('DELETE', `/rootfolders/${id}`),
  browse: (folderPath?: string | null) =>
    request<FolderListing>('GET', `/filesystem${folderPath ? `?path=${encodeURIComponent(folderPath)}` : ''}`),

  importCandidates: (rootFolderId: number) => request<ImportCandidate[]>('GET', `/library-import/${rootFolderId}`),
  importFolders: (
    rootFolderId: number,
    monitored: boolean,
    qualityProfileId: number | null,
    items: { folderName: string; tmdbId: number }[],
  ) => request<{ queued: number }>('POST', '/library-import', { rootFolderId, monitored, qualityProfileId, items }),

  stats: () => request<LibraryStats>('GET', '/library/stats'),
  rescan: () => request<JobStatus>('POST', '/library/rescan'),
  refreshAll: () => request<{ queued: number }>('POST', '/library/refresh-all'),
  jobs: () => request<JobStatus>('GET', '/system/jobs'),

  metadataSettings: () => request<MetadataSettings>('GET', '/settings/metadata'),
  saveMetadataSettings: (tmdbToken: string | null, language: string) =>
    request<MetadataSettings>('PUT', '/settings/metadata', { tmdbToken, language }),
  testMetadata: (tmdbToken: string | null) => request<{ ok: boolean }>('POST', '/settings/metadata/test', { tmdbToken }),
}

export const queryKeys = {
  movies: ['movies'] as const,
  movie: (id: number) => ['movie', id] as const,
  series: ['series'] as const,
  seriesDetail: (id: number) => ['series-detail', id] as const,
  rootFolders: ['root-folders'] as const,
  stats: ['library-stats'] as const,
  jobs: ['jobs'] as const,
  metadata: ['metadata-settings'] as const,
}
