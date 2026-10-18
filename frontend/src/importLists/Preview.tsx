import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { importListApi, importListKeys, type PreviewItem, type PreviewState } from '../api/importLists'
import { queryKeys } from '../api/library'
import ui from '../components/ui.module.css'
import { Pill } from '../indexers/Chips'
import { mediaLabel, stateLabels, tmdbUrl } from './labels'
import styles from './ImportLists.module.css'

const order: PreviewState[] = ['new', 'waiting', 'inLibrary', 'excluded', 'notFound']
const tone: Record<PreviewState, 'ok' | 'warn' | 'error' | 'off' | 'info'> = {
  new: 'ok',
  waiting: 'info',
  inLibrary: 'off',
  excluded: 'warn',
  notFound: 'error',
}

/**
 * What the list would add (and what it skips, and why). "Exclude" keeps an item out of every list; "Add" adds it now
 * with the list's options (saved lists only).
 */
export default function Preview({
  name,
  items: initial,
  listId,
  canEdit,
}: {
  name: string | null
  items: PreviewItem[]
  /** Saved list: items can be added from here. */
  listId: number | null
  canEdit: boolean
}) {
  const queryClient = useQueryClient()
  const [items, setItems] = useState(initial)
  const ref = useRef<HTMLDivElement>(null)

  // The preview opens below the form: bring it into view.
  useEffect(() => ref.current?.scrollIntoView({ behavior: 'smooth', block: 'nearest' }), [])
  const [filter, setFilter] = useState<PreviewState | null>(null)
  const [error, setError] = useState<string | null>(null)

  const setState = (item: PreviewItem, state: PreviewState) => setItems((all) => all.map((i) => (i === item ? { ...i, state } : i)))

  const exclude = useMutation({
    mutationFn: (item: PreviewItem) => importListApi.addExclusion(item.mediaType, item.tmdbId!, item.title, item.year),
    onSuccess: async (_, item) => {
      setState(item, 'excluded')
      setError(null)
      await queryClient.invalidateQueries({ queryKey: importListKeys.exclusions })
    },
    onError: (e) => setError(e.message),
  })
  const add = useMutation({
    mutationFn: (item: PreviewItem) => importListApi.add(listId!, item.tmdbId!),
    onSuccess: async (_, item) => {
      setState(item, 'inLibrary')
      setError(null)
      await queryClient.invalidateQueries({ queryKey: item.mediaType === 'Series' ? queryKeys.series : queryKeys.movies })
    },
    onError: (e) => setError(e.message),
  })

  const counts = order.map((s) => [s, items.filter((i) => i.state === s).length] as const).filter(([, n]) => n > 0)
  const shown = items.filter((i) => filter === null || i.state === filter).sort((a, b) => order.indexOf(a.state) - order.indexOf(b.state))
  const busy = exclude.isPending || add.isPending

  return (
    <div ref={ref} className={styles.preview} aria-live="polite">
      <div className={styles.previewHead}>
        <span className={styles.previewTitle}>
          {name ? `“${name}”` : 'Preview'} · {items.length} {items.length === 1 ? 'item' : 'items'}
        </span>
        <span className={styles.counts}>
          {counts.map(([state, n]) => (
            <button
              key={state}
              type="button"
              className={styles.filter}
              aria-pressed={filter === state}
              onClick={() => setFilter(filter === state ? null : state)}
            >
              {stateLabels[state]} {n}
            </button>
          ))}
        </span>
      </div>
      {error && <p className={`${ui.error} ${styles.empty}`}>{error}</p>}
      {shown.length === 0 ? (
        <p className={styles.empty}>The list is empty.</p>
      ) : (
        <ul className={styles.previewList}>
          {shown.map((item, index) => (
            <li key={`${item.tmdbId ?? item.imdbId ?? item.title}-${index}`} className={styles.previewItem}>
              <span className={styles.itemTitle}>
                {item.tmdbId ? (
                  <a href={tmdbUrl(item.mediaType, item.tmdbId)} target="_blank" rel="noreferrer" title={`${mediaLabel(item.mediaType)} on TMDB`}>
                    {item.title || `TMDB ${item.tmdbId}`}
                  </a>
                ) : (
                  item.title || item.imdbId
                )}{' '}
                {item.year > 0 && <span className={styles.year}>({item.year})</span>}
              </span>
              <Pill tone={tone[item.state]} title={item.state === 'waiting' && item.availableFrom ? `Available from ${item.availableFrom}` : undefined}>
                {item.state === 'waiting' && item.availableFrom ? `From ${item.availableFrom}` : stateLabels[item.state]}
              </Pill>
              {canEdit && item.tmdbId && (item.state === 'new' || item.state === 'waiting') && (
                <span className={styles.itemActions}>
                  {listId && (
                    <button className={`${ui.button} ${styles.small}`} type="button" disabled={busy} onClick={() => add.mutate(item)}>
                      Add
                    </button>
                  )}
                  <button className={`${ui.button} ${styles.small}`} type="button" disabled={busy} onClick={() => exclude.mutate(item)}>
                    Exclude
                  </button>
                </span>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
