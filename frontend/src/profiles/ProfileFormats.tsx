import { useQuery } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { Link } from 'react-router'
import { customFormatKeys, customFormatsApi, formatFormatScore, type FormatMediaType } from '../api/customFormats'
import type { ProfileFormatItem, QualityProfile } from '../api/profiles'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import { advancedLabel } from '../lib/advancedMode'
import styles from './ProfileDialog.module.css'

const presets = [
  { value: -10000, label: 'Never grab (−10,000)' },
  { value: -1000, label: 'Avoid (−1,000)' },
  { value: -100, label: 'Slightly avoid (−100)' },
  { value: 0, label: 'Ignore (0)' },
  { value: 100, label: 'Slightly prefer (+100)' },
  { value: 1000, label: 'Prefer (+1,000)' },
  { value: 10000, label: 'Strongly prefer (+10,000)' },
]

/** «Custom formats» part of the profile dialog: score limits and a score per format, movie and series formats apart. */
export default function ProfileFormats({
  draft,
  update,
  canEdit,
  advanced,
  template,
  errors,
}: {
  draft: QualityProfile
  update: (change: Partial<QualityProfile>) => void
  canEdit: boolean
  advanced: boolean
  /** Template scores by format id (TRaSH-managed profiles), to show and reset changed scores. */
  template: Map<number, number> | null
  errors: Record<string, string[]>
}) {
  const formats = useQuery({ queryKey: customFormatKeys.all, queryFn: customFormatsApi.list, staleTime: 60_000 })
  const items = useMemo(() => draft.formatItems ?? [], [draft.formatItems])
  const [type, setType] = useState<FormatMediaType>(() => (items.some((i) => i.mediaType === 'movie') ? 'movie' : 'series'))
  const [onlyScored, setOnlyScored] = useState(() => items.filter((i) => i.score !== 0).length > 8)
  const [search, setSearch] = useState('')

  const trashIds = useMemo(() => new Map((formats.data ?? []).map((f) => [f.id, f.trashId])), [formats.data])
  const counts = {
    movie: items.filter((i) => i.mediaType === 'movie' && i.score !== 0).length,
    series: items.filter((i) => i.mediaType === 'series' && i.score !== 0).length,
  }
  const totals = { movie: items.filter((i) => i.mediaType === 'movie').length, series: items.filter((i) => i.mediaType === 'series').length }

  // Sorted once by the scores at opening, so rows do not jump while typing.
  const [order] = useState(() => new Map([...items].sort((a, b) => b.score - a.score || a.name.localeCompare(b.name)).map((i, n) => [i.customFormatId, n])))
  const rows = useMemo(() => {
    const words = search.toLowerCase().split(/\s+/).filter(Boolean)
    return items
      .filter((i) => i.mediaType === type)
      .filter((i) => !onlyScored || i.score !== 0 || (template?.get(i.customFormatId) ?? 0) !== 0)
      .filter((i) => words.every((w) => i.name.toLowerCase().includes(w)))
      .sort((a, b) => (order.get(a.customFormatId) ?? 1e9) - (order.get(b.customFormatId) ?? 1e9) || a.name.localeCompare(b.name))
  }, [items, type, onlyScored, search, order, template])

  const setScore = (id: number, score: number) =>
    update({ formatItems: items.map((i) => (i.customFormatId === id ? { ...i, score } : i)) })

  const changed = template ? items.filter((i) => template.has(i.customFormatId) && template.get(i.customFormatId) !== i.score) : []

  const applyAll = (change: (i: ProfileFormatItem) => number) =>
    update({ formatItems: items.map((i) => (i.mediaType === type ? { ...i, score: change(i) } : i)) })

  return (
    <div className={styles.formats}>
      <div className={styles.scoreGrid}>
        <ScoreField
          label="Minimum score"
          help="Releases below are not grabbed."
          value={draft.minFormatScore ?? 0}
          onChange={(v) => update({ minFormatScore: v })}
          error={errors.minFormatScore?.[0]}
        />
        <ScoreField
          label="Upgrade until score"
          help="Stop upgrading at the cutoff quality with this score. 0: quality only."
          value={draft.cutoffFormatScore ?? 0}
          onChange={(v) => update({ cutoffFormatScore: v })}
          error={errors.cutoffFormatScore?.[0]}
          disabled={!draft.upgradeAllowed}
        />
        {(advanced || !!errors.minUpgradeFormatScore) && (
          <ScoreField
            label="Minimum upgrade step"
            help="An upgrade of the same quality must raise the score by this much."
            value={draft.minUpgradeFormatScore ?? 1}
            onChange={(v) => update({ minUpgradeFormatScore: v })}
            error={errors.minUpgradeFormatScore?.[0]}
            min={1}
            expert
          />
        )}
      </div>

      {template && changed.length > 0 && (
        <p className={styles.changedNote}>
          <span className={styles.changedDot} />
          {changed.length} score{changed.length === 1 ? ' differs' : 's differ'} from the TRaSH template.
          {canEdit && (
            <button
              type="button"
              className={`${ui.ghost} ${ui.sm}`}
              onClick={() => update({ formatItems: items.map((i) => (template.has(i.customFormatId) ? { ...i, score: template.get(i.customFormatId)! } : i)) })}
            >
              <Icon name="undo" size={13} />
              Reset all
            </button>
          )}
        </p>
      )}

      <div className={styles.formatTools}>
        <div className={ui.seg} role="group" aria-label="Formats for">
          {(['movie', 'series'] as const).map((t) => (
            <button key={t} type="button" aria-pressed={type === t} onClick={() => setType(t)}>
              <Icon name={t === 'movie' ? 'film' : 'tv'} size={14} />
              {t === 'movie' ? 'Movies' : 'Series'}
              <span className={styles.segCount}>
                {counts[t]}/{totals[t]}
              </span>
            </button>
          ))}
        </div>
        <label className={styles.onlyScored}>
          <span className={ui.switch}>
            <input type="checkbox" role="switch" checked={onlyScored} onChange={(e) => setOnlyScored(e.target.checked)} />
            <span aria-hidden="true" />
          </span>
          Only scored
        </label>
        <label className={styles.formatSearch}>
          <Icon name="search" size={14} />
          <input className={ui.input} type="search" placeholder="Find" aria-label="Find a format" value={search} onChange={(e) => setSearch(e.target.value)} />
        </label>
        {canEdit && advanced && (
          <select
            className={`${ui.select} ${styles.bulk}`}
            value=""
            aria-label="Change all scores in this list"
            onChange={(e) => {
              if (e.target.value === 'clear') applyAll(() => 0)
              if (e.target.value === 'template' && template) applyAll((i) => template.get(i.customFormatId) ?? i.score)
              e.target.value = ''
            }}
          >
            <option value="">All {type === 'movie' ? 'movie' : 'series'} scores…</option>
            {template && <option value="template">Back to the TRaSH scores</option>}
            <option value="clear">Set all to 0</option>
          </select>
        )}
      </div>

      {totals.movie + totals.series === 0 ? (
        <div className={styles.formatEmpty}>
          <p>No custom formats yet.</p>
          <Link to="/settings/custom-formats" className={`${ui.button} ${ui.sm}`}>
            <Icon name="tag" size={14} />
            Set up custom formats
          </Link>
        </div>
      ) : rows.length === 0 ? (
        <p className={styles.formatNone}>{onlyScored ? 'No format has a score here yet. Turn off “Only scored” to give one.' : 'No format matches.'}</p>
      ) : (
        <ul className={styles.formatList} aria-label={type === 'movie' ? 'Movie format scores' : 'Series format scores'}>
          {rows.map((i) => {
            const base = template?.get(i.customFormatId)
            const differs = base !== undefined && base !== i.score
            return (
              <li key={i.customFormatId} className={i.score === 0 ? styles.unscored : undefined}>
                <span className={styles.formatName}>
                  {i.name}
                  {!template && trashIds.get(i.customFormatId) && <span className={styles.trashTag}>TRaSH</span>}
                </span>
                {differs && (
                  <span className={styles.changed} title={`The TRaSH template gives ${formatFormatScore(base)}`}>
                    <span className={styles.changedDot} />
                    <span className={styles.changedText}>TRaSH {formatFormatScore(base)}</span>
                    {canEdit && (
                      <button type="button" className={styles.reset} onClick={() => setScore(i.customFormatId, base)} aria-label={`Reset ${i.name} to ${base}`}>
                        <Icon name="undo" size={12} />
                      </button>
                    )}
                  </span>
                )}
                {canEdit && (
                  <select
                    className={styles.quick}
                    value=""
                    aria-label={`Quick score for ${i.name}`}
                    title="Quick score"
                    onChange={(e) => {
                      if (e.target.value !== '') setScore(i.customFormatId, Number(e.target.value))
                      e.target.value = ''
                    }}
                  >
                    <option value="">⋯</option>
                    {presets.map((p) => (
                      <option key={p.value} value={p.value}>
                        {p.label}
                      </option>
                    ))}
                  </select>
                )}
                <input
                  className={`${ui.input} ${ui.num} ${styles.scoreInput} ${i.score > 0 ? styles.scorePlus : i.score < 0 ? styles.scoreMinus : ''}`}
                  type="number"
                  step={1}
                  inputMode="numeric"
                  value={Number.isNaN(i.score) ? '' : i.score}
                  onChange={(e) => setScore(i.customFormatId, e.target.value === '' ? 0 : Math.round(e.target.valueAsNumber))}
                  aria-label={`Score for ${i.name}`}
                  disabled={!canEdit}
                />
              </li>
            )
          })}
        </ul>
      )}
      {errors.formatItems && <small className={ui.error}>{errors.formatItems[0]}</small>}
    </div>
  )
}

function ScoreField({
  label,
  help,
  value,
  onChange,
  error,
  min,
  disabled,
  expert,
}: {
  label: string
  help: string
  value: number
  onChange: (value: number) => void
  error?: string
  min?: number
  disabled?: boolean
  expert?: boolean
}) {
  return (
    <label className={`${ui.field} ${styles.scoreField}`}>
      <span className={expert ? advancedLabel : undefined}>{label}</span>
      <input
        className={`${ui.input} ${ui.num}`}
        type="number"
        step={1}
        min={min}
        inputMode="numeric"
        value={value}
        disabled={disabled}
        onChange={(e) => onChange(e.target.value === '' ? 0 : Math.round(e.target.valueAsNumber))}
      />
      <small>{help}</small>
      {error && <small className={ui.error}>{error}</small>}
    </label>
  )
}
