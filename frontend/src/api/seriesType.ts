import { request } from './client'
import type { Series } from './library'

/** Sonarr's series type: how episodes are named, searched and matched. */
export type SeriesType = 'standard' | 'daily' | 'anime'

export const seriesTypes: { value: SeriesType; label: string; help: string }[] = [
  { value: 'standard', label: 'Standard', help: 'Season and episode numbers (S01E05).' },
  { value: 'daily', label: 'Daily', help: 'Air dates (2026.10.18), for talk shows and news.' },
  { value: 'anime', label: 'Anime', help: 'Absolute episode numbers (Title - 26) and scene numbering.' },
]

export const seriesTypeApi = {
  set: (id: number, seriesType: SeriesType) => request<Series>('PUT', `/series/${id}`, { seriesType }),
}
