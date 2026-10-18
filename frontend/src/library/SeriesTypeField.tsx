import { useMutation, useQueryClient } from '@tanstack/react-query'
import { queryKeys, type Series } from '../api/library'
import { seriesTypeApi, seriesTypes, type SeriesType } from '../api/seriesType'
import ui from '../components/ui.module.css'
import { advancedLabel, useAdvanced } from '../lib/advancedMode'
import styles from './SeriesType.module.css'

/**
 * «Series type» in the add dialog. Simple mode shows it only when the series looks like anime or a daily show (then
 * preselected); Advanced always shows it.
 */
export function SeriesTypeField({
  value,
  suggested,
  onChange,
}: {
  value: SeriesType
  suggested: SeriesType | null | undefined
  onChange: (value: SeriesType) => void
}) {
  const advanced = useAdvanced()
  const notable = (suggested ?? 'standard') !== 'standard' || value !== 'standard'
  if (!advanced && !notable) return null

  const help = seriesTypes.find((t) => t.value === value)?.help
  return (
    <label className={ui.field}>
      <span className={notable ? undefined : advancedLabel}>Series type</span>
      <select className={ui.select} value={value} onChange={(e) => onChange(e.target.value as SeriesType)}>
        {seriesTypes.map((t) => (
          <option key={t.value} value={t.value}>
            {t.label}
            {t.value === suggested && t.value !== 'standard' ? ' (detected)' : ''}
          </option>
        ))}
      </select>
      {help && <small>{help}</small>}
    </label>
  )
}

/** «Series type» on the series page: changes how episodes are named, searched and matched. */
export function SeriesTypeSelect({ series }: { series: Series }) {
  const advanced = useAdvanced()
  const queryClient = useQueryClient()
  const change = useMutation({
    mutationFn: (seriesType: SeriesType) => seriesTypeApi.set(series.id, seriesType),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.seriesDetail(series.id) })
      await queryClient.invalidateQueries({ queryKey: queryKeys.series })
    },
  })

  if (!advanced && series.seriesType === 'standard') return null

  return (
    <>
      <select
        className={`${ui.select} ${styles.select}`}
        value={series.seriesType}
        onChange={(e) => change.mutate(e.target.value as SeriesType)}
        disabled={change.isPending}
        aria-label="Series type"
        title={seriesTypes.find((t) => t.value === series.seriesType)?.help}
      >
        {seriesTypes.map((t) => (
          <option key={t.value} value={t.value}>
            {t.label}
          </option>
        ))}
      </select>
      {change.error && <span className={ui.error}>{change.error.message}</span>}
    </>
  )
}
