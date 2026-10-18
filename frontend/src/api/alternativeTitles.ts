import { request } from './client'
import { queryKeys, type MediaType } from './library'

/** Where an alternative title comes from: TMDB, a TMDB translation, a scene name (TheXEM) or added by you. */
export type AlternativeTitleSource = 'tmdb' | 'translation' | 'xem' | 'user'

export interface AlternativeTitle {
  id: number
  title: string
  source: AlternativeTitleSource
  /** Language name, e.g. "German"; null when unknown. */
  language: string | null
  /** Series: the title only counts for this season. */
  seasonNumber: number | null
  /** Series: the scene season this name stands for (TheXEM). */
  sceneSeasonNumber: number | null
}

/** The library item a release name belongs to and the title that matched (release test page, manual import). */
export interface LibraryMatch {
  mediaType: 'movie' | 'series'
  id: number
  title: string
  year: number
  matchedTitle: string
  /** "title", "originalTitle", "id" or an alternative title source. */
  source: string
  language: string | null
}

const base = (type: MediaType, id: number) => `/${type === 'Movie' ? 'movies' : 'series'}/${id}/titles`

/** Below the item's own key, so a library change of the item refreshes its titles too. */
export const alternativeTitleKeys = {
  list: (type: MediaType, id: number) => [...(type === 'Movie' ? queryKeys.movie(id) : queryKeys.seriesDetail(id)), 'titles'] as const,
}

export const alternativeTitlesApi = {
  list: (type: MediaType, id: number) => request<AlternativeTitle[]>('GET', base(type, id)),
  add: (type: MediaType, id: number, title: string, seasonNumber: number | null) =>
    request<AlternativeTitle>('POST', base(type, id), { title, seasonNumber }),
  remove: (type: MediaType, id: number, titleId: number) => request<void>('DELETE', `${base(type, id)}/${titleId}`),
  /** Every alternative title of the library, for the command palette. */
  all: () => request<{ mediaType: 'movie' | 'series'; id: number; title: string }[]>('GET', '/library/titles'),
  match: (title: string) => request<{ match: LibraryMatch | null }>('GET', `/library/match?title=${encodeURIComponent(title)}`),
}
