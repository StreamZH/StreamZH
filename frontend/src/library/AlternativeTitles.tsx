import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { alternativeTitleKeys, alternativeTitlesApi, type AlternativeTitle, type AlternativeTitleSource } from '../api/alternativeTitles'
import { ApiError } from '../api/client'
import type { MediaType } from '../api/library'
import Icon from '../components/Icon'
import { Skeleton } from '../components/Loader'
import ui from '../components/ui.module.css'
import { useIsAdmin } from '../status/useOngoing'
import styles from './AlternativeTitles.module.css'

const sources: Record<AlternativeTitleSource, { label: string; help: string; order: number }> = {
  user: { label: 'Yours', help: 'Added by you; kept on every refresh', order: 0 },
  translation: { label: 'Translation', help: 'TMDB translation', order: 1 },
  xem: { label: 'Scene', help: 'Scene name from TheXEM', order: 2 },
  tmdb: { label: 'TMDB', help: 'TMDB alternative title', order: 3 },
}

/** Short codes for the languages titles usually come in; others show their name. */
const codes: Record<string, string> = {
  German: 'DE', English: 'EN', French: 'FR', Spanish: 'ES', 'Spanish (Latino)': 'ES-LA', Italian: 'IT', Dutch: 'NL', Danish: 'DA',
  Swedish: 'SV', Norwegian: 'NO', Finnish: 'FI', Polish: 'PL', Portuguese: 'PT', 'Portuguese (Brazil)': 'PT-BR', Japanese: 'JA',
  Korean: 'KO', Chinese: 'ZH', Russian: 'RU', Turkish: 'TR', Czech: 'CS', Hungarian: 'HU', Greek: 'EL',
}

const storageKey = 'streamzh.altTitles.open'

function readOpen(): boolean {
  try {
    return localStorage.getItem(storageKey) === '1'
  } catch {
    return false
  }
}

function writeOpen(open: boolean) {
  try {
    localStorage.setItem(storageKey, open ? '1' : '0')
  } catch {
    // private window: the panel just starts closed
  }
}

function seasonLabel(t: AlternativeTitle): string | null {
  if (t.sceneSeasonNumber != null) return `Scene S${String(t.sceneSeasonNumber).padStart(2, '0')}`
  if (t.seasonNumber != null) return `S${String(t.seasonNumber).padStart(2, '0')}`
  return null
}

/**
 * "Also known as": the other names a movie or series is matched and searched by (Radarr's alternative titles, Sonarr's
 * scene names). TMDB titles, translations and scene names come with every refresh; you can add your own.
 */
export default function AlternativeTitles({ type, id, seasons }: { type: MediaType; id: number; seasons?: number[] }) {
  const admin = useIsAdmin()
  const client = useQueryClient()
  const key = alternativeTitleKeys.list(type, id)
  const titles = useQuery({ queryKey: key, queryFn: () => alternativeTitlesApi.list(type, id) })
  const [open, setOpen] = useState(readOpen)
  const [draft, setDraft] = useState('')
  const [season, setSeason] = useState('')

  const add = useMutation({
    mutationFn: () => alternativeTitlesApi.add(type, id, draft.trim(), season === '' ? null : Number(season)),
    onSuccess: () => {
      setDraft('')
      setSeason('')
      void client.invalidateQueries({ queryKey: key })
    },
  })
  const remove = useMutation({
    mutationFn: (titleId: number) => alternativeTitlesApi.remove(type, id, titleId),
    onSuccess: () => void client.invalidateQueries({ queryKey: key }),
  })

  const list = [...(titles.data ?? [])].sort(
    (a, b) => sources[a.source].order - sources[b.source].order || a.title.localeCompare(b.title),
  )
  const preview = list.slice(0, 3).map((t) => t.title)
  const fieldError = add.error instanceof ApiError ? Object.values(add.error.fieldErrors).flat()[0] : add.error?.message
  const toggle = () => {
    setOpen(!open)
    writeOpen(!open)
  }
  const submit = (e: FormEvent) => {
    e.preventDefault()
    if (draft.trim()) add.mutate()
  }

  return (
    <section className={`${ui.panel} ${styles.panel}`}>
      <button type="button" className={styles.head} aria-expanded={open} onClick={toggle}>
        <span className={styles.title}>
          <Icon name="tag" size={17} />
          Also known as
        </span>
        <span className={styles.count}>{titles.data ? list.length : '…'}</span>
        {!open && preview.length > 0 && (
          <span className={styles.preview}>
            {preview.join(' · ')}
            {list.length > preview.length && ' …'}
          </span>
        )}
        <Icon name="down" size={16} className={`${styles.chevron} ${open ? styles.chevronOpen : ''}`} />
      </button>

      {open && (
        <div className={styles.body}>
          <p className={styles.lead}>
            Release names in these titles count as this {type === 'Movie' ? 'movie' : 'series'}, and searches use some of them.
          </p>
          {titles.isPending && <Skeleton rows={1} />}
          {titles.isError && <p className={ui.error}>{titles.error.message}</p>}
          {titles.data && list.length === 0 && <p className={ui.muted}>No other titles yet. They come with the next refresh from TMDB.</p>}
          {list.length > 0 && (
            <ul className={styles.chips}>
              {list.map((t) => {
                const source = sources[t.source]
                const seasonText = seasonLabel(t)
                return (
                  <li key={t.id} className={`${styles.chip} ${t.source === 'user' ? styles.mine : ''}`}>
                    <span className={styles.chipTitle}>{t.title}</span>
                    {t.language && (
                      <span className={styles.lang} title={t.language}>
                        {codes[t.language] ?? t.language}
                      </span>
                    )}
                    {seasonText && <span className={styles.season}>{seasonText}</span>}
                    <span className={`${styles.source} ${styles[`src_${t.source}`] ?? ''}`} title={source.help}>
                      {source.label}
                    </span>
                    {admin && t.source === 'user' && (
                      <button
                        type="button"
                        className={styles.remove}
                        aria-label={`Remove ${t.title}`}
                        title="Remove"
                        disabled={remove.isPending}
                        onClick={() => remove.mutate(t.id)}
                      >
                        <Icon name="x" size={13} />
                      </button>
                    )}
                  </li>
                )
              })}
            </ul>
          )}
          {remove.error && <p className={ui.error}>{remove.error.message}</p>}

          {admin && (
            <form className={styles.form} onSubmit={submit}>
              <input
                className={`${ui.input} ${styles.input}`}
                value={draft}
                onChange={(e) => setDraft(e.target.value)}
                placeholder="Add your own title, e.g. the German one"
                aria-label="Your own title"
                maxLength={200}
                aria-invalid={fieldError ? true : undefined}
              />
              {type === 'Series' && (
                <select className={`${ui.select} ${styles.seasonSelect}`} value={season} onChange={(e) => setSeason(e.target.value)} aria-label="Season">
                  <option value="">All seasons</option>
                  {[...(seasons ?? [])].sort((a, b) => a - b).map((n) => (
                    <option key={n} value={n}>
                      {n === 0 ? 'Specials' : `Season ${n}`}
                    </option>
                  ))}
                </select>
              )}
              <button className={`${ui.button} ${ui.sm}`} type="submit" disabled={!draft.trim() || add.isPending}>
                <Icon name="plus" size={14} />
                Add
              </button>
              {fieldError && <p className={`${ui.error} ${styles.formError}`}>{fieldError}</p>}
            </form>
          )}
        </div>
      )}
    </section>
  )
}
