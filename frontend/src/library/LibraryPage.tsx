import { useQuery } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { Link } from 'react-router'
import { libraryApi, queryKeys, type MediaType, type Movie, type Series } from '../api/library'
import { useAuth } from '../auth/auth'
import Poster from '../components/Poster'
import ui from '../components/ui.module.css'
import { formatBytes } from '../lib/format'
import page from '../pages/Page.module.css'
import { useIsAdmin } from '../status/useOngoing'
import AddDialog from './AddDialog'
import styles from './Library.module.css'
import MassEditBar from './MassEditBar'
import { UndoButton } from './RunProgress'
import { SetupHint } from './SetupHint'
import { useOrganizeStatus } from './useOrganizeStatus'

type Sort = 'title' | 'year' | 'added'
type Filter = 'all' | 'missing' | 'ondisk' | 'unmonitored'

interface Item {
  id: number
  title: string
  year: number
  sortTitle: string
  added: string
  posterPath: string | null
  monitored: boolean
  status: 'ondisk' | 'missing' | 'unmonitored' | 'partial' | 'loading'
  detail: string
  folder: string
  size: number
}

export default function LibraryPage({ type }: { type: MediaType }) {
  const isMovie = type === 'Movie'
  const { user } = useAuth()
  const [adding, setAdding] = useState(false)
  const [search, setSearch] = useState('')
  const [sort, setSort] = useState<Sort>('title')
  const [filter, setFilter] = useState<Filter>('all')
  const admin = useIsAdmin()
  const [selecting, setSelecting] = useState(false)
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const organize = useOrganizeStatus()

  const movies = useQuery({ queryKey: queryKeys.movies, queryFn: libraryApi.movies, enabled: isMovie })
  const series = useQuery({ queryKey: queryKeys.series, queryFn: libraryApi.series, enabled: !isMovie })
  const rootFolders = useQuery({ queryKey: queryKeys.rootFolders, queryFn: libraryApi.rootFolders })
  const metadata = useQuery({ queryKey: queryKeys.metadata, queryFn: libraryApi.metadataSettings, enabled: user?.role !== 'Friend' })

  const source = isMovie ? movies : series
  const items = useMemo(
    () => (isMovie ? (movies.data ?? []).map(movieItem) : (series.data ?? []).map(seriesItem)),
    [isMovie, movies.data, series.data],
  )

  const visible = useMemo(() => {
    const term = search.trim().toLowerCase()
    return items
      .filter((i) => !term || i.title.toLowerCase().includes(term))
      .filter((i) => {
        if (filter === 'missing') return i.status === 'missing' || i.status === 'partial'
        if (filter === 'ondisk') return i.status === 'ondisk' || i.status === 'partial'
        if (filter === 'unmonitored') return !i.monitored
        return true
      })
      .sort((a, b) => {
        if (sort === 'year') return b.year - a.year || a.sortTitle.localeCompare(b.sortTitle)
        if (sort === 'added') return b.added.localeCompare(a.added)
        return a.sortTitle.localeCompare(b.sortTitle)
      })
  }, [items, search, sort, filter])

  const chosen = items.filter((i) => selected.has(i.id))
  const toggle = (id: number) =>
    setSelected((s) => {
      const next = new Set(s)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  const stopSelecting = () => {
    setSelecting(false)
    setSelected(new Set())
  }
  const undo = organize.data?.undo?.mediaType === type ? organize.data.undo : null
  const running = organize.data?.running?.mediaType === type ? organize.data.running : null

  const folders = (rootFolders.data ?? []).filter((r) => r.mediaType === type)
  const unmapped = folders.reduce((sum, r) => sum + r.unmappedFolders, 0)
  const noun = isMovie ? 'movie' : 'series'
  const tmdbMissing = metadata.data && !metadata.data.tmdbConfigured
  const noRoot = rootFolders.data && folders.length === 0
  const canAdd = !tmdbMissing && !noRoot

  return (
    <div className={page.page}>
      <header className={styles.head}>
        <div>
          <h1 className={page.title}>{isMovie ? 'Movies' : 'Series'}</h1>
          <p className={page.lead}>
            {source.data ? `${items.length} in library` : 'Loading…'}
            {!isMovie && series.data && ` · ${series.data.reduce((s, x) => s + x.fileCount, 0)} episodes on disk`}
            {' · '}
            {formatBytes(
              isMovie
                ? (movies.data ?? []).reduce((s, m) => s + (m.file?.size ?? 0), 0)
                : (series.data ?? []).reduce((s, x) => s + x.sizeOnDisk, 0),
            )}
          </p>
        </div>
        <div className={styles.actions}>
          {user?.role !== 'Friend' && unmapped > 0 && (
            <Link className={ui.button} to={`/library/import?type=${type}`}>
              Import {unmapped} existing
            </Link>
          )}
          {admin && items.length > 0 && (
            <button
              className={ui.button}
              type="button"
              aria-pressed={selecting}
              onClick={() => (selecting ? stopSelecting() : setSelecting(true))}
            >
              {selecting ? 'Cancel selection' : 'Select'}
            </button>
          )}
          <button className={ui.primary} type="button" onClick={() => setAdding(true)} disabled={!canAdd}>
            + Add {noun}
          </button>
        </div>
      </header>

      <SetupHint tmdbMissing={!!tmdbMissing} noRoot={!!noRoot} type={type} />

      {admin && (undo || running) && (
        <div className={styles.runBanner} role="status">
          {running ? (
            <span>
              {running.title}
              {running.started ? ` · ${running.done} of ${running.total}` : ' · waiting'}
            </span>
          ) : (
            undo && (
              <>
                <span>
                  Last organize renamed {undo.items} {isMovie ? (undo.items === 1 ? 'movie' : 'movies') : 'series'}. Undo is possible until{' '}
                  {new Date(undo.until).toLocaleString([], { weekday: 'short', hour: '2-digit', minute: '2-digit' })}.
                </span>
                <UndoButton status={organize.data} label="Undo last organize" />
              </>
            )
          )}
        </div>
      )}

      {items.length > 0 && (
        <div className={styles.toolbar}>
          <input
            className={ui.input}
            type="search"
            placeholder={`Filter ${isMovie ? 'movies' : 'series'}`}
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
          <select className={ui.select} value={filter} onChange={(e) => setFilter(e.target.value as Filter)} aria-label="Show">
            <option value="all">All</option>
            <option value="ondisk">On disk</option>
            <option value="missing">Missing</option>
            <option value="unmonitored">Unmonitored</option>
          </select>
          <select className={ui.select} value={sort} onChange={(e) => setSort(e.target.value as Sort)} aria-label="Sort">
            <option value="title">Title</option>
            <option value="year">Year</option>
            <option value="added">Recently added</option>
          </select>
          {selecting && (
            <div className={styles.selectTools}>
              <button
                className={ui.button}
                type="button"
                onClick={() => setSelected((s) => new Set([...s, ...visible.map((i) => i.id)]))}
                disabled={visible.every((i) => selected.has(i.id))}
              >
                Select {visible.length === items.length ? 'all' : 'shown'} ({visible.length})
              </button>
              <button className={ui.button} type="button" onClick={() => setSelected(new Set())} disabled={selected.size === 0}>
                Select none
              </button>
            </div>
          )}
        </div>
      )}

      {source.isError && <p className={ui.error}>{source.error.message}</p>}

      {source.data && items.length === 0 && canAdd && (
        <div className={`${page.panel} ${styles.empty}`}>
          <p>
            Your {isMovie ? 'movie' : 'series'} library is empty. Add a {noun}
            {unmapped > 0 ? ` or import the ${unmapped} folders already in your root folder` : ''}.
          </p>
        </div>
      )}

      {visible.length > 0 && (
        <ul className={`${styles.grid} ${selecting ? styles.selecting : ''}`}>
          {visible.map((item) => {
            const content = (
              <>
                <Poster path={item.posterPath} title={item.title} />
                <span className={`${styles.bar} ${styles[item.status]}`} />
                <span className={styles.cardTitle}>{item.title}</span>
                <span className={styles.cardMeta}>
                  {item.year > 0 ? item.year : '–'} · {item.detail}
                </span>
              </>
            )
            return (
              <li key={item.id}>
                {selecting ? (
                  <label className={`${styles.card} ${styles.selectable} ${selected.has(item.id) ? styles.selected : ''}`}>
                    <input
                      className={styles.pick}
                      type="checkbox"
                      checked={selected.has(item.id)}
                      onChange={() => toggle(item.id)}
                      aria-label={`Select ${item.title}`}
                    />
                    {content}
                  </label>
                ) : (
                  <Link className={styles.card} to={`/library/${isMovie ? 'movies' : 'series'}/${item.id}`}>
                    {content}
                  </Link>
                )}
              </li>
            )
          })}
        </ul>
      )}

      {items.length > 0 && visible.length === 0 && <p className={ui.muted}>Nothing matches this filter.</p>}

      {selecting && (
        <MassEditBar
          type={type}
          selected={chosen.map((i) => ({ id: i.id, title: i.year > 0 ? `${i.title} (${i.year})` : i.title, folder: i.folder, size: i.size }))}
          onClear={stopSelecting}
          onDone={() => setSelected(new Set())}
        />
      )}

      {adding && <AddDialog type={type} onClose={() => setAdding(false)} />}
    </div>
  )
}

function movieItem(m: Movie): Item {
  return {
    id: m.id,
    title: m.title,
    year: m.year,
    sortTitle: m.title.toLowerCase().replace(/^(the|a|an|der|die|das) /, ''),
    added: m.added,
    posterPath: m.posterPath,
    monitored: m.monitored,
    status: m.hasFile ? 'ondisk' : m.monitored ? 'missing' : 'unmonitored',
    detail: m.hasFile ? 'On disk' : m.monitored ? 'Missing' : 'Unmonitored',
    folder: m.folderPath,
    size: m.file?.size ?? 0,
  }
}

function seriesItem(s: Series): Item {
  const loading = s.lastRefreshed === null
  const complete = s.airedCount > 0 && s.fileCount >= s.airedCount
  return {
    id: s.id,
    title: s.title,
    year: s.year,
    sortTitle: s.title.toLowerCase().replace(/^(the|a|an|der|die|das) /, ''),
    added: s.added,
    posterPath: s.posterPath,
    monitored: s.monitored,
    status: loading ? 'loading' : complete ? 'ondisk' : s.fileCount > 0 ? 'partial' : s.monitored ? 'missing' : 'unmonitored',
    detail: loading ? 'Loading episodes…' : `${s.fileCount} / ${s.airedCount} episodes`,
    folder: s.folderPath,
    size: s.sizeOnDisk,
  }
}
