import { request } from './client'
import type { JobStatus } from './library'

/** Settings → Metadata: TMDB, certification country, NFO title and the local image cache. */
export interface MetadataSettings {
  tmdbConfigured: boolean
  tmdbTokenHint: string | null
  language: string
  languages: string[]
  /** ISO country code whose age rating goes into NFO files and {Movie Certification}. */
  certificationCountry: string
  certificationCountries: string[]
  /** NFO <title> is the original title instead of the translated one. */
  nfoOriginalTitle: boolean
  /** Posters go through StreamZH and are kept in <data>/MediaCover. */
  imageCache: boolean
}

export interface MetadataSettingsChange {
  /** Null keeps the saved token. */
  tmdbToken: string | null
  language: string
  certificationCountry: string
  nfoOriginalTitle: boolean
  imageCache: boolean
}

/** The Kodi (XBMC) / Emby metadata writer (NFO files and artwork next to the media). */
export interface KodiMetadataOptions {
  enabled: boolean
  movieMetadata: boolean
  movieMetadataUrl: boolean
  movieImages: boolean
  useMovieNfo: boolean
  movieCollection: boolean
  seriesMetadata: boolean
  seriesMetadataUrl: boolean
  episodeMetadata: boolean
  seriesImages: boolean
  seasonImages: boolean
  episodeImages: boolean
}

export const metadataKeys = {
  // Same key as the library page uses for the TMDB status.
  settings: ['metadata-settings'] as const,
  kodi: ['metadata-kodi'] as const,
}

export const metadataApi = {
  settings: () => request<MetadataSettings>('GET', '/settings/metadata'),
  save: (change: MetadataSettingsChange) => request<MetadataSettings>('PUT', '/settings/metadata', change),
  test: (tmdbToken: string | null) => request<{ ok: boolean }>('POST', '/settings/metadata/test', { tmdbToken }),
  kodi: () => request<KodiMetadataOptions>('GET', '/settings/metadata/kodi'),
  saveKodi: (options: KodiMetadataOptions) => request<KodiMetadataOptions>('PUT', '/settings/metadata/kodi', options),
  writeLibrary: () => request<JobStatus>('POST', '/metadata/write'),
}
