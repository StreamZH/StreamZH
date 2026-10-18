import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { ApiError } from '../api/client'
import { customFormatKeys, formatFormatScore, type FormatMediaType } from '../api/customFormats'
import { profileKeys } from '../api/profiles'
import {
  namingPresets,
  namingVariants,
  plainLines,
  shortDescription,
  splitGroupName,
  trashApi,
  trashKeys,
  type TrashGroup,
  type TrashNaming,
  type TrashProfileDetail,
  type TrashProfileSummary,
} from '../api/trash'
import Icon from '../components/Icon'
import Loader from '../components/Loader'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import { AdvancedToggle } from '../lib/Advanced'
import { useAdvanced } from '../lib/advancedMode'
import styles from './Trash.module.css'

type Step = 1 | 2 | 3 | 4

const stepNames = ['Movies', 'Series', 'Extras', 'Preview']

const groupOrder = ['Standard', 'German', 'French', 'Anime', 'SQP']

/** «Create profile from TRaSH»: movie template, series template, extra format groups and naming, preview, apply. */
export default function TrashWizard({ onClose }: { onClose: () => void }) {
  const queryClient = useQueryClient()
  const advanced = useAdvanced()
  const [step, setStep] = useState<Step>(1)
  const [name, setName] = useState('')
  const [nameTouched, setNameTouched] = useState(false)
  const [movie, setMovie] = useState<string | null>(null)
  const [series, setSeries] = useState<string | null | undefined>(undefined)
  const [extras, setExtras] = useState<Record<FormatMediaType, string[] | null>>({ movie: null, series: null })
  const [qualitySizes, setQualitySizes] = useState(true)
  const [namingKey, setNamingKey] = useState('keep')
  const [naming, setNaming] = useState<TrashNaming>({})

  const movieList = useQuery({ queryKey: trashKeys.profiles('movie'), queryFn: () => trashApi.profiles('movie'), staleTime: 5 * 60_000 })
  const seriesList = useQuery({ queryKey: trashKeys.profiles('series'), queryFn: () => trashApi.profiles('series'), staleTime: 5 * 60_000 })
  const movieGroups = useQuery({ queryKey: trashKeys.groups('movie'), queryFn: () => trashApi.groups('movie'), enabled: step >= 3 && !!movie, staleTime: 5 * 60_000 })
  const seriesGroups = useQuery({ queryKey: trashKeys.groups('series'), queryFn: () => trashApi.groups('series'), enabled: step >= 3 && !!series, staleTime: 5 * 60_000 })
  const movieDetail = useQuery({
    queryKey: trashKeys.profile('movie', movie ?? ''),
    queryFn: () => trashApi.profile('movie', movie!),
    enabled: step === 4 && !!movie,
    staleTime: 5 * 60_000,
  })
  const seriesDetail = useQuery({
    queryKey: trashKeys.profile('series', series ?? ''),
    queryFn: () => trashApi.profile('series', series!),
    enabled: step === 4 && !!series,
    staleTime: 5 * 60_000,
  })

  const movieSummary = movieList.data?.find((p) => p.trashId === movie)
  const seriesSummary = seriesList.data?.find((p) => p.trashId === series)

  const pickMovie = (id: string | null) => {
    setMovie(id)
    setExtras((e) => ({ ...e, movie: null }))
    if (!nameTouched) setName(movieList.data?.find((p) => p.trashId === id)?.name ?? '')
  }

  const toSeries = () => {
    // Preselect the series template with the same slug (e.g. german-hd-bluray-web), once.
    if (series === undefined) {
      const match = movieSummary ? seriesList.data?.find((p) => p.slug === movieSummary.slug || p.name === movieSummary.name) : undefined
      setSeries(match?.trashId ?? null)
      if (!nameTouched && !movie && match) setName(match.name)
    }
    setStep(2)
  }

  const chosenGroups = (type: FormatMediaType, groups: TrashGroup[] | undefined, template: string | null | undefined) =>
    extras[type] ?? applicable(groups, template).filter((g) => g.default).map((g) => g.trashId)

  const apply = useMutation({
    mutationFn: () =>
      trashApi.apply({
        profileId: null,
        name: name.trim(),
        movieTemplate: movie ?? undefined,
        seriesTemplate: series ?? undefined,
        extraGroups: [...(movie ? chosenGroups('movie', movieGroups.data, movie) : []), ...(series ? chosenGroups('series', seriesGroups.data, series) : [])],
        applyQualitySizes: qualitySizes,
        naming: namingKey === 'keep' ? undefined : namingKey === 'custom' ? naming : namingPresets.find((p) => p.key === namingKey)?.naming,
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: profileKeys.profiles })
      await queryClient.invalidateQueries({ queryKey: customFormatKeys.all })
      await queryClient.invalidateQueries({ queryKey: trashKeys.status })
    },
  })
  const fieldErrors = apply.error instanceof ApiError ? apply.error.fieldErrors : {}

  const canNext = step === 1 ? true : step === 2 ? !!movie || !!series : step === 3 ? true : !!name.trim() && (!!movie || !!series)

  if (apply.isSuccess) {
    return (
      <Modal title="Profile created" onClose={onClose}>
        <div className={`${styles.wizard} ${styles.done}`}>
          <span className={styles.doneIcon}>
            <Icon name="check" size={22} />
          </span>
          <h3>{apply.data.profile.name}</h3>
          <p className={styles.text}>
            {apply.data.created} custom format{apply.data.created === 1 ? '' : 's'} added and {apply.data.updated} updated from the TRaSH Guides. Assign
            the profile to movies and series, or make it the default.
          </p>
          <div className={styles.foot}>
            <button className={ui.primary} type="button" onClick={onClose}>
              Done
            </button>
          </div>
        </div>
      </Modal>
    )
  }

  return (
    <Modal title="Create profile from TRaSH" onClose={onClose}>
      <div className={styles.wizard}>
        <ol className={styles.steps} aria-label="Steps">
          {stepNames.map((label, i) => {
            const n = (i + 1) as Step
            const state = n === step ? styles.stepOn : n < step ? styles.stepDone : ''
            return (
              <li key={label} className={state} aria-current={n === step ? 'step' : undefined}>
                <span className={styles.stepNum}>{n < step ? <Icon name="check" size={12} /> : n}</span>
                <span className={styles.stepLabel}>{label}</span>
              </li>
            )
          })}
        </ol>

        {step === 1 && (
          <>
            <p className={styles.text}>
              Pick the movie template. It sets the qualities, the custom formats and their scores. You can add a series template next.
            </p>
            <TemplatePicker
              type="movie"
              list={movieList.data}
              loading={movieList.isPending}
              error={movieList.error?.message}
              value={movie}
              onChange={pickMovie}
            />
          </>
        )}

        {step === 2 && (
          <>
            <p className={styles.text}>
              Pick the series template, or none. One profile serves both: movie formats score movies, series formats score episodes.
              {seriesSummary && movieSummary && seriesSummary.slug === movieSummary.slug && <> The matching one is already selected.</>}
            </p>
            <TemplatePicker
              type="series"
              list={seriesList.data}
              loading={seriesList.isPending}
              error={seriesList.error?.message}
              value={series ?? null}
              onChange={setSeries}
            />
          </>
        )}

        {step === 3 && (
          <div className={styles.extras}>
            {movie && (
              <GroupPicker
                title={`Movie extras · ${movieSummary?.name ?? ''}`}
                groups={applicable(movieGroups.data, movie)}
                loading={movieGroups.isPending}
                error={movieGroups.error?.message}
                chosen={chosenGroups('movie', movieGroups.data, movie)}
                onChange={(ids) => setExtras((e) => ({ ...e, movie: ids }))}
              />
            )}
            {series && (
              <GroupPicker
                title={`Series extras · ${seriesSummary?.name ?? ''}`}
                groups={applicable(seriesGroups.data, series)}
                loading={seriesGroups.isPending}
                error={seriesGroups.error?.message}
                chosen={chosenGroups('series', seriesGroups.data, series)}
                onChange={(ids) => setExtras((e) => ({ ...e, series: ids }))}
              />
            )}
            <div className={styles.extraRows}>
              <label className={styles.switchRow}>
                <span className={ui.switch}>
                  <input type="checkbox" role="switch" checked={qualitySizes} onChange={(e) => setQualitySizes(e.target.checked)} />
                  <span aria-hidden="true" />
                </span>
                <span>
                  Use the TRaSH quality sizes
                  <small>Replaces the size limits under Settings → Quality with the guides' values.</small>
                </span>
              </label>
              <div className={styles.namingRow}>
                <label className={ui.field}>
                  <span>File and folder naming</span>
                  <select className={ui.select} value={namingKey} onChange={(e) => setNamingKey(e.target.value)}>
                    {namingPresets.map((p) => (
                      <option key={p.key} value={p.key}>
                        {p.label}
                      </option>
                    ))}
                    {advanced && <option value="custom">Choose each format…</option>}
                  </select>
                  <small>The TRaSH naming includes {'{Custom Formats}'}, so tags like German DL end up in file names.</small>
                </label>
                <AdvancedToggle />
              </div>
              {advanced && namingKey === 'custom' && (
                <div className={styles.namingGrid}>
                  {(Object.keys(namingVariants) as (keyof typeof namingVariants)[]).map((key) => (
                    <label key={key} className={ui.field}>
                      <span>{namingLabels[key]}</span>
                      <select className={ui.select} value={naming[key] ?? ''} onChange={(e) => setNaming({ ...naming, [key]: e.target.value || undefined })}>
                        <option value="">Keep mine</option>
                        {namingVariants[key].map((v) => (
                          <option key={v} value={v}>
                            {v}
                          </option>
                        ))}
                      </select>
                    </label>
                  ))}
                </div>
              )}
            </div>
          </div>
        )}

        {step === 4 && (
          <div className={styles.preview}>
            <label className={ui.field}>
              <span>Profile name</span>
              <input
                className={ui.input}
                value={name}
                maxLength={100}
                onChange={(e) => {
                  setName(e.target.value)
                  setNameTouched(true)
                }}
              />
              {fieldErrors.name && <small className={ui.error}>{fieldErrors.name[0]}</small>}
            </label>
            {movie && <TemplatePreview type="movie" detail={movieDetail.data} loading={movieDetail.isPending} error={movieDetail.error?.message} extraCount={chosenGroups('movie', movieGroups.data, movie).length} />}
            {series && (
              <TemplatePreview type="series" detail={seriesDetail.data} loading={seriesDetail.isPending} error={seriesDetail.error?.message} extraCount={chosenGroups('series', seriesGroups.data, series).length} />
            )}
            <p className={styles.note}>
              {qualitySizes ? 'Quality sizes are replaced. ' : ''}
              {namingKey === 'keep' ? 'Naming stays as it is.' : 'Naming is changed under Settings → Media Management.'}
            </p>
          </div>
        )}

        {apply.error && Object.keys(fieldErrors).length === 0 && <p className={ui.errBox}>{apply.error.message}</p>}

        <div className={styles.foot}>
          {step > 1 && (
            <button className={ui.ghost} type="button" onClick={() => setStep((s) => (s - 1) as Step)}>
              <Icon name="left" size={14} />
              Back
            </button>
          )}
          <span className={styles.spacer} />
          {step === 1 && <span className={styles.footHint}>{movie ? movieSummary?.name : 'No movie template'}</span>}
          {step < 4 ? (
            <button className={ui.primary} type="button" disabled={!canNext} onClick={() => (step === 1 ? toSeries() : setStep((s) => (s + 1) as Step))}>
              Next
              <Icon name="right" size={14} />
            </button>
          ) : (
            <button className={ui.primary} type="button" disabled={!canNext || apply.isPending} onClick={() => apply.mutate()}>
              <Icon name="sparkle" size={15} />
              {apply.isPending ? 'Creating…' : 'Create profile'}
            </button>
          )}
        </div>
      </div>
    </Modal>
  )
}

const namingLabels: Record<keyof typeof namingVariants, string> = {
  movieFolder: 'Movie folder',
  movieFile: 'Movie file',
  seriesFolder: 'Series folder',
  seriesFile: 'Episode file',
}

/** Groups meant for this template (all of them when the server does not say). */
function applicable(groups: TrashGroup[] | undefined, template: string | null | undefined): TrashGroup[] {
  if (!groups) return []
  return groups.filter((g) => !g.profiles || g.profiles.length === 0 || (template && g.profiles.includes(template)))
}

function TemplatePicker({
  type,
  list,
  loading,
  error,
  value,
  onChange,
}: {
  type: FormatMediaType
  list: TrashProfileSummary[] | undefined
  loading: boolean
  error?: string
  value: string | null
  onChange: (id: string | null) => void
}) {
  // Opens on the group of the chosen template, so a preselected one is in view.
  const [group, setGroup] = useState(() => list?.find((p) => p.trashId === value)?.group ?? 'all')
  const [search, setSearch] = useState('')
  const groups = useMemo(() => {
    const names = [...new Set((list ?? []).map((p) => p.group))]
    return names.sort((a, b) => (groupOrder.indexOf(a) + 1 || 99) - (groupOrder.indexOf(b) + 1 || 99) || a.localeCompare(b))
  }, [list])
  const words = search.toLowerCase().split(/\s+/).filter(Boolean)
  const shown = (list ?? []).filter(
    (p) => (group === 'all' || p.group === group) && words.every((w) => `${p.name} ${p.description} ${p.language ?? ''} ${p.group}`.toLowerCase().includes(w)),
  )
  const byGroup = groups.map((g) => ({ g, items: shown.filter((p) => p.group === g) })).filter((x) => x.items.length > 0)

  if (loading) return <Loader text="Loading the templates…" />
  if (error) return <p className={ui.errBox}>{error}</p>

  return (
    <div className={styles.picker}>
      <div className={styles.pickerTools}>
        <div className={styles.groupChips} role="group" aria-label="Template group">
          <button type="button" aria-pressed={group === 'all'} onClick={() => setGroup('all')}>
            All <span>{list?.length ?? 0}</span>
          </button>
          {groups.map((g) => (
            <button key={g} type="button" aria-pressed={group === g} onClick={() => setGroup(g)}>
              {g} <span>{list?.filter((p) => p.group === g).length}</span>
            </button>
          ))}
        </div>
        <label className={styles.pickerSearch}>
          <Icon name="search" size={14} />
          <input className={ui.input} type="search" placeholder="Find, e.g. german 1080p" aria-label="Find a template" value={search} onChange={(e) => setSearch(e.target.value)} />
        </label>
      </div>
      <div className={styles.templates} role="radiogroup" aria-label={type === 'movie' ? 'Movie template' : 'Series template'}>
        <label className={`${styles.template} ${styles.templateNone} ${value === null ? styles.templateOn : ''}`}>
          <input type="radio" name={`tpl-${type}`} checked={value === null} onChange={() => onChange(null)} />
          <span className={styles.templateText}>
            <strong>No {type === 'movie' ? 'movie' : 'series'} template</strong>
            <small>{type === 'movie' ? 'Only series formats and qualities.' : 'Only movie formats and qualities.'}</small>
          </span>
        </label>
        {byGroup.map(({ g, items }) => (
          <div key={g} className={styles.templateGroup}>
            {group === 'all' && <h4 className={styles.templateGroupTitle}>{g}</h4>}
            {items.map((p) => {
              const description = shortDescription(p.description)
              return (
                <label key={p.trashId} className={`${styles.template} ${value === p.trashId ? styles.templateOn : ''}`}>
                  <input type="radio" name={`tpl-${type}`} checked={value === p.trashId} onChange={() => onChange(p.trashId)} />
                  <span className={styles.templateText}>
                    <span className={styles.templateHead}>
                      <strong>{p.name}</strong>
                      {p.language && p.language !== 'Any' && <span className={styles.lang}>{p.language}</span>}
                      {p.url && (
                        <a href={p.url} target="_blank" rel="noreferrer" className={styles.guide} onClick={(e) => e.stopPropagation()} title="Open in the TRaSH Guides">
                          <Icon name="ext" size={12} />
                        </a>
                      )}
                    </span>
                    {description && <small>{description}</small>}
                  </span>
                </label>
              )
            })}
          </div>
        ))}
        {byGroup.length === 0 && <p className={styles.note}>No template matches “{search}”.</p>}
      </div>
    </div>
  )
}

function GroupPicker({
  title,
  groups,
  loading,
  error,
  chosen,
  onChange,
}: {
  title: string
  groups: TrashGroup[]
  loading: boolean
  error?: string
  chosen: string[]
  onChange: (ids: string[]) => void
}) {
  const [open, setOpen] = useState<string | null>(null)
  const categories = useMemo(() => {
    const map = new Map<string, TrashGroup[]>()
    for (const g of groups) {
      const { category } = splitGroupName(g.name)
      map.set(category, [...(map.get(category) ?? []), g])
    }
    // Required and recommended groups first.
    const rank = (list: TrashGroup[], name: string) => (/required/i.test(name) ? 0 : list.some((g) => g.default) ? 1 : 2)
    return [...map.entries()].sort((a, b) => rank(a[1], a[0]) - rank(b[1], b[0]) || a[0].localeCompare(b[0]))
  }, [groups])
  return (
    <section className={styles.groupBox}>
      <h4 className={styles.groupBoxTitle}>
        {title}
        <span className={styles.groupBoxCount}>
          {chosen.length} of {groups.length}
        </span>
      </h4>
      {loading && <Loader text="Loading the format groups…" />}
      {error && <p className={ui.errBox}>{error}</p>}
      {categories.map(([category, list]) => (
        <div key={category} className={styles.groupCategory}>
          <h5 className={styles.groupCategoryTitle}>{category}</h5>
          <ul className={styles.groupList}>
            {list.map((g) => {
              const on = chosen.includes(g.trashId)
              const lines = plainLines(g.description)
              return (
                <li key={g.trashId} className={on ? styles.groupOn : undefined}>
                  <label className={styles.groupLabel}>
                    <input type="checkbox" checked={on} onChange={(e) => onChange(e.target.checked ? [...chosen, g.trashId] : chosen.filter((id) => id !== g.trashId))} />
                    <span className={styles.groupText}>
                      <span className={styles.groupName}>
                        {splitGroupName(g.name).name}
                        {g.default && <span className={styles.recommended}>Recommended</span>}
                      </span>
                      {lines[0] && <small>{lines[0]}</small>}
                    </span>
                  </label>
                  <button
                    type="button"
                    className={`${ui.ghost} ${ui.sm} ${styles.groupMore}`}
                    onClick={() => setOpen(open === g.trashId ? null : g.trashId)}
                    aria-expanded={open === g.trashId}
                  >
                    {g.formats.length} format{g.formats.length === 1 ? '' : 's'}
                    <Icon name="down" size={12} className={`${styles.chevron} ${open === g.trashId ? styles.chevronOpen : ''}`} />
                  </button>
                  {open === g.trashId && (
                    <span className={styles.groupFormats}>
                      {g.formats.map((f) => (
                        <span key={f.trashId} className={`${styles.formatChip} ${f.required ? styles.formatRequired : ''}`} title={f.required ? 'Always added with this group' : undefined}>
                          {f.name}
                        </span>
                      ))}
                    </span>
                  )}
                </li>
              )
            })}
          </ul>
        </div>
      ))}
    </section>
  )
}

function TemplatePreview({
  type,
  detail,
  loading,
  error,
  extraCount,
}: {
  type: FormatMediaType
  detail: TrashProfileDetail | undefined
  loading: boolean
  error?: string
  extraCount: number
}) {
  const [all, setAll] = useState(false)
  if (loading) return <Loader text="Reading the template…" />
  if (error || !detail) return <p className={ui.errBox}>{error ?? 'The template could not be read.'}</p>
  const allowed = (detail.items ?? []).filter((i) => i.allowed)
  const formats = [...(detail.formats ?? [])].sort((a, b) => b.score - a.score || a.name.localeCompare(b.name))
  const shown = all ? formats : formats.slice(0, 10)
  return (
    <section className={styles.previewBox}>
      <h4 className={styles.previewTitle}>
        <Icon name={type === 'movie' ? 'film' : 'tv'} size={15} />
        {detail.name}
      </h4>
      <dl className={styles.previewFacts}>
        <div>
          <dt>Qualities</dt>
          <dd className={styles.qualityChips}>
            {allowed.map((i) => (
              <span key={i.name} className={`${styles.qualityChip} ${i.name === detail.cutoff ? styles.qualityCutoff : ''}`} title={i.items?.length ? i.items.join(', ') : undefined}>
                {i.name}
                {i.items && i.items.length > 0 && <span className={styles.qualityCount}>{i.items.length}</span>}
              </span>
            ))}
          </dd>
        </div>
        <div>
          <dt>Upgrade until</dt>
          <dd>
            {detail.upgradeAllowed === false ? 'No upgrades' : (detail.cutoff ?? '–')}
            {detail.cutoffFormatScore ? ` · score ${detail.cutoffFormatScore.toLocaleString('en-US')}` : ''}
          </dd>
        </div>
        <div>
          <dt>Minimum score</dt>
          <dd>{(detail.minFormatScore ?? 0).toLocaleString('en-US')}</dd>
        </div>
      </dl>
      <ul className={styles.previewFormats}>
        {shown.map((f) => (
          <li key={f.trashId}>
            <span>{f.name}</span>
            <span className={`${styles.previewScore} ${f.score > 0 ? styles.plus : f.score < 0 ? styles.minus : ''}`}>{formatFormatScore(f.score)}</span>
          </li>
        ))}
      </ul>
      <div className={styles.previewMore}>
        {formats.length > 10 && (
          <button type="button" className={`${ui.ghost} ${ui.sm}`} onClick={() => setAll(!all)}>
            {all ? 'Show fewer' : `Show all ${formats.length} formats`}
          </button>
        )}
        {extraCount > 0 && (
          <span className={styles.note}>
            + {extraCount} extra group{extraCount === 1 ? '' : 's'} with the guides' scores
          </span>
        )}
      </div>
    </section>
  )
}
