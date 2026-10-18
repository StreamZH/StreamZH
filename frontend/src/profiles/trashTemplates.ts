import { useQuery } from '@tanstack/react-query'
import { customFormatKeys, customFormatsApi } from '../api/customFormats'
import type { QualityProfile } from '../api/profiles'
import { trashApi, trashKeys } from '../api/trash'

const long = { staleTime: 5 * 60_000, retry: false } as const

/** Names of the TRaSH templates by trash id, movie and series, for the "TRaSH: …" pills. */
export function useTemplateNames(enabled: boolean): Map<string, string> {
  const movie = useQuery({ queryKey: trashKeys.profiles('movie'), queryFn: () => trashApi.profiles('movie'), enabled, ...long })
  const series = useQuery({ queryKey: trashKeys.profiles('series'), queryFn: () => trashApi.profiles('series'), enabled, ...long })
  return new Map([...(movie.data ?? []), ...(series.data ?? [])].map((p) => [p.trashId, p.name]))
}

/**
 * The scores the profile's TRaSH templates give, by custom format id; null for own profiles or while loading. Scores
 * that differ from these were changed by the user.
 */
export function useTemplateScores(profile: Pick<QualityProfile, 'trashMovieTemplate' | 'trashSeriesTemplate'>): Map<number, number> | null {
  const movieId = profile.trashMovieTemplate ?? null
  const seriesId = profile.trashSeriesTemplate ?? null
  const managed = !!movieId || !!seriesId
  const formats = useQuery({ queryKey: customFormatKeys.all, queryFn: customFormatsApi.list, enabled: managed, staleTime: 60_000 })
  const movie = useQuery({
    queryKey: trashKeys.profile('movie', movieId ?? ''),
    queryFn: () => trashApi.profile('movie', movieId!),
    enabled: !!movieId,
    ...long,
  })
  const series = useQuery({
    queryKey: trashKeys.profile('series', seriesId ?? ''),
    queryFn: () => trashApi.profile('series', seriesId!),
    enabled: !!seriesId,
    ...long,
  })
  if (!managed || !formats.data || (movieId && !movie.data) || (seriesId && !series.data)) return null
  const scores = new Map<number, number>()
  for (const [type, detail] of [['movie', movie.data], ['series', series.data]] as const) {
    for (const t of detail?.formats ?? []) {
      const format = formats.data.find((f) => f.trashId === t.trashId && f.mediaType === type)
      if (format) scores.set(format.id, t.score)
    }
  }
  return scores
}

/** Formats whose score differs from the template. */
export function changedScores(profile: QualityProfile, template: Map<number, number> | null): number[] {
  if (!template) return []
  return (profile.formatItems ?? []).filter((f) => template.has(f.customFormatId) && template.get(f.customFormatId) !== f.score).map((f) => f.customFormatId)
}
