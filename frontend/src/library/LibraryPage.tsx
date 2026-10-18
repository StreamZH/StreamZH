import { useQuery } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { Link } from 'react-router'
import { downloadKeys, downloadsApi, withLive, type DownloadItem } from '../api/downloads'
import { libraryApi, queryKeys, type MediaType, type Movie, type Series } from '../api/library'
import { useAuth } from '../auth/auth'
import Icon from '../components/Icon'
import { Skeleton } from '../components/Loader'
import PageHeader from '../components/PageHeader'
import Poster from '../components/Poster'
import ui from '../components/ui.module.css'
import { formatBytes } from '../lib/format'
import page from '../pages/Page.module.css'
import { useIsAdmin, useLiveProgress } from '../status/useOngoing'
import AddDialog from './AddDialog'
import styles from './Library.module.css'
import MassEditBar from './MassEditBar'
import { UndoButton } from './RunProgress'
import { SetupHint } from './SetupHint'
import { useOrganizeStatus } from './useOrganizeStatus'

type Sort = 'title' | 'year' | 'added'
type Filter = 'all' | 'missing' | 'ondisk' | 'unmonitored'
type Status = 'ondisk' | 'missing' | 'unmonitored' | 'partial' | 'loading'

interface Item {
  id: number
  title: string
  year: number
  sortTitle: string
  added: string
  posterPath: string | null
  monitored: boolean
  status: Status
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
  // Same query as the sidebar counts and the top bar's download pill (the shell fetches them; nothing extra here).
  const stats = useQuery({ queryKey: queryKeys.stats, queryFn: libraryApi.stats })
  const queue = useQuery({ queryKey: downloadKeys.queue, queryFn: downloadsApi.queue, enabled: false })
  const live = useLiveProgress()

  const source = isMovie ? movies : series
  const items = useMemo(
    () => (isMovie ? (movies.data ?? []).map(movieItem) : (series.data ?? []).map(seriesItem)),
    [isMovie, movies.data, series.data],
  )

  // Downloads in the queue per movie or series: the poster shows "↓ 42 %".
  const downloading = useMemo(() => {
    const map = new Map<number, number>()
    for (const raw of queue.data?.items ?? []) {
      const item: DownloadItem = withLive(raw, live, queue.dataUpdatedAt)
      const ref = isMovie ? item.movieId : item.seriesId
      if (ref == null || item.status === 'completed' || item.status === 'failed') continue
      map.set(ref, Math.max(map.get(ref) ?? 0, item.progress))
    }
    return map
  }, [queue.data, queue.dataUpdatedAt, live, isMovie])

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
  const totalSize = isMovie
    ? (movies.data ?? []).reduce((s, m) => s + (m.file?.size ?? 0), 0)
    : (series.data ?? []).reduce((s, x) => s + x.sizeOnDisk, 0)
  const movieCount = isMovie && movies.data ? movies.data.length : stats.data?.movies
  const seriesCount = !isMovie && series.data ? series.data.length : stats.data?.series

  const lead = source.data ? (
    <span className={ui.num}>
      {items.length} in library
      {!isMovie && series.data && ` · ${series.data.reduce((s, x) => s + x.fileCount, 0)} episodes on disk`}
      {totalSize > 0 && ` · ${formatBytes(totalSize)}`}
    </span>
  ) : (
    <span className={ui.muted}>Loading the library…</span>
  )

  return (
    <div className={page.page}>
      <PageHeader
        title={isMovie ? 'Movies' : 'Series'}
        lead={lead}
        actions={
          <>
            {user?.role !== 'Friend' && unmapped > 0 && (
              <Link className={ui.button} to={`/library/import?type=${type}`}>
                <Icon name="inbox" size={15} />
                Import {unmapped} existing
              </Link>
            )}
            {admin && items.length > 0 && (
              <button
                className={selecting ? ui.ghost : ui.button}
                type="button"
                aria-pressed={selecting}
                onClick={() => (selecting ? stopSelecting() : setSelecting(true))}
              >
                {selecting ? <Icon name="x" size={15} /> : <Icon name="check" size={15} />}
                {selecting ? 'Cancel selection' : 'Select'}
              </button>
            )}
            <button className={ui.primary} type="button" onClick={() => setAdding(true)} disabled={!canAdd}>
              <Icon name="plus" size={15} />
              Add {noun}
            </button>
          </>
        }
      />

      <SetupHint tmdbMissing={!!tmdbMissing} noRoot={!!noRoot} type={type} />

      {admin && (undo || running) && (
        <div className={styles.runBanner} role="status">
          <Icon name={running ? 'rename' : 'undo'} size={16} className={styles.runIcon} />
          {running ? (
            <span className={ui.num}>
              {running.title}
              {running.started ? ` · ${running.done} of ${running.total} · ${Math.floor(running.progress * 100)} %` : ' · waiting'}
            </span>
          ) : (
            undo && (
              <>
                <span>
                  Last organize renamed {undo.items} {isMovie ? (undo.items === 1 ? 'movie' : 'movies') : 'series'}. Undo is possible until{' '}
                  {new Date(undo.until).toLocaleString([], { weekday: 'short', hour: '2-digit', minute: '2-digit' })}.
                </span>
                <span className={styles.runAction}>
                  <UndoButton status={organize.data} label="Undo last organize" />
                </span>
              </>
            )
          )}
        </div>
      )}

      <div className={styles.toolbar}>
        <nav className={`${ui.seg} ${styles.kinds}`} aria-label="Library">
          <Link to="/library/movies" className={isMovie ? ui.segOn : undefined} aria-current={isMovie ? 'page' : undefined}>
            Movies
            {movieCount != null && <span className={styles.segCount}>{movieCount}</span>}
          </Link>
          <Link to="/library/series" className={isMovie ? undefined : ui.segOn} aria-current={isMovie ? undefined : 'page'}>
            Series
            {seriesCount != null && <span className={styles.segCount}>{seriesCount}</span>}
          </Link>
        </nav>
        {items.length > 0 && (
          <>
            <select className={`${ui.select} ${styles.compact}`} value={filter} onChange={(e) => setFilter(e.target.value as Filter)} aria-label="Show">
              <option value="all">All</option>
              <option value="ondisk">On disk</option>
              <option value="missing">Missing</option>
              <option value="unmonitored">Unmonitored</option>
            </select>
            <select className={`${ui.select} ${styles.compact}`} value={sort} onChange={(e) => setSort(e.target.value as Sort)} aria-label="Sort">
              <option value="title">Title</option>
              <option value="year">Year</option>
              <option value="added">Recently added</option>
            </select>
            <label className={styles.search}>
              <Icon name="search" size={15} />
              <input
                className={ui.input}
                type="search"
                placeholder={`Search ${isMovie ? 'movies' : 'series'}`}
                aria-label={`Search ${isMovie ? 'movies' : 'series'}`}
                value={search}
                onChange={(e) => setSearch(e.target.value)}
              />
            </label>
          </>
        )}
      </div>

      {source.isError && <p className={ui.error}>{source.error.message}</p>}
      {source.isPending && !source.isError && <Skeleton rows={3} />}

      {source.data && items.length === 0 && canAdd && (
        <div className={`${ui.panel} ${ui.empty}`}>
          <Icon name={isMovie ? 'film' : 'tv'} size={22} />
          <h3>Still empty</h3>
          <span>
            Add a {noun}
            {unmapped > 0 ? ` or import the ${unmapped} folders already in your root folder` : ''}.
          </span>
        </div>
      )}

      {visible.length > 0 && (
        <ul className={`${styles.grid} ${selecting ? styles.selecting : ''}`}>
          {visible.map((item) => {
            const progress = downloading.get(item.id)
            const content = (
              <>
                <span className={styles.poster}>
                  <Poster path={item.posterPath} title={item.title} />
                  <Flag status={item.status} progress={progress} />
                </span>
                <span className={styles.text}>
                  <span className={styles.cardTitle}>{item.title}</span>
                  <span className={styles.cardMeta}>{[item.year > 0 ? String(item.year) : '–', item.detail].filter(Boolean).join(' · ')}</span>
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

      {items.length > 0 && visible.length === 0 && (
        <div className={`${ui.panel} ${ui.empty}`}>
          <Icon name="search" size={22} />
          <h3>Nothing found</h3>
          <span>Nothing matches this search or filter.</span>
        </div>
      )}

      {selecting && (
        <MassEditBar
          type={type}
          selected={chosen.map((i) => ({ id: i.id, title: i.year > 0 ? `${i.title} (${i.year})` : i.title, folder: i.folder, size: i.size }))}
          onClear={stopSelecting}
          onDone={() => setSelected(new Set())}
          selectAll={{
            label: `${visible.length === items.length ? 'All' : 'All shown'} (${visible.length})`,
            disabled: visible.every((i) => selected.has(i.id)),
            onClick: () => setSelected((s) => new Set([...s, ...visible.map((i) => i.id)])),
          }}
          onSelectNone={() => setSelected(new Set())}
        />
      )}

      {adding && <AddDialog type={type} onClose={() => setAdding(false)} />}
    </div>
  )
}

/** The calm status mark in the poster's corner: a dot when all is there, a short pill otherwise. */
function Flag({ status, progress }: { status: Status; progress: number | undefined }) {
  if (progress != null)
    return (
      <span className={`${styles.flag} ${styles.flagInfo}`} title="Downloading">
        <Icon name="download" size={11} />
        <span className={ui.num}>{Math.floor(Math.min(1, Math.max(0, progress)) * 100)} %</span>
      </span>
    )
  switch (status) {
    case 'ondisk':
      return <span className={`${styles.flag} ${styles.flagDot}`} title="On disk" aria-label="On disk" />
    case 'missing':
      return <span className={`${styles.flag} ${styles.flagErr}`}>Missing</span>
    case 'partial':
      return <span className={`${styles.flag} ${styles.flagWarn}`}>Incomplete</span>
    case 'loading':
      return <span className={`${styles.flag} ${styles.flagMuted}`}>Loading</span>
    case 'unmonitored':
      return <span className={`${styles.flag} ${styles.flagMuted}`}>Unmonitored</span>
  }
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
    detail: m.hasFile ? `${m.file ? `${formatBytes(m.file.size)} · ` : ''}On disk` : m.monitored ? 'Missing' : 'Unmonitored',
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
    detail: loading ? 'Loading episodes…' : `${s.fileCount}/${s.airedCount} episodes${s.sizeOnDisk > 0 ? ` · ${formatBytes(s.sizeOnDisk)}` : ''}`,
    folder: s.folderPath,
    size: s.sizeOnDisk,
  }
}
