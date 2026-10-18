import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useLocation, useSearchParams } from 'react-router'
import {
  invalidateWanted,
  wantedApi,
  wantedKeys,
  type SortDirection,
  type WantedFilter,
  type WantedKind,
  type WantedRecord,
  type WantedSortKey,
  type WantedType,
} from '../api/wanted'
import { useToasts } from '../activity/hooks'
import Icon from '../components/Icon'
import { Skeleton } from '../components/Loader'
import Modal from '../components/Modal'
import PageHeader from '../components/PageHeader'
import ui from '../components/ui.module.css'
import Toasts from '../indexerTools/Toast'
import { formatDate, formatDateTime, relativeTime, tmdbImage, useUiFormats } from '../lib/format'
import { useIsAdmin } from '../status/useOngoing'
import styles from './Wanted.module.css'

const pageSizes = [25, 50, 100]
const sortKeys: WantedSortKey[] = ['title', 'date', 'lastSearched', 'status', 'profile', 'quality']
const directions: SortDirection[] = ['ascending', 'descending']

const queueLabels = { downloading: 'Downloading', waitingForImport: 'Waiting for import', grabbed: 'Grabbed' } as const

const reasonLabels = { quality: 'Below cutoff', language: 'Wrong language', dualLanguage: 'Not dual language', formatScore: 'Below format score' } as const

const texts = {
  missing: {
    title: 'Missing',
    lead: 'Monitored movies and episodes without a file that are released, like Wanted → Missing in Radarr and Sonarr.',
    searchAll: 'Search all missing',
    searchShort: 'Search all',
  },
  cutoff: {
    title: 'Cutoff unmet',
    lead: 'Files below the quality cutoff or in the wrong language of their profile. A search grabs an upgrade when one is found.',
    searchAll: 'Search all cutoff unmet',
    searchShort: 'Search all',
  },
} as const

function noun(type: WantedType, count: number): string {
  if (type === 'movies') return count === 1 ? 'movie' : 'movies'
  return count === 1 ? 'episode' : 'episodes'
}

function episodeNumber(r: WantedRecord): string {
  return `${r.seasonNumber}x${String(r.episodeNumber).padStart(2, '0')}`
}

function detailLink(r: WantedRecord): string {
  return r.type === 'movie' ? `/library/movies/${r.movieId}` : `/library/series/${r.seriesId}`
}

/** The earlier of the digital and physical release, with which one it is. */
function homeRelease(r: WantedRecord): { date: string; label: string } | null {
  const dates = [
    r.digitalRelease ? { date: r.digitalRelease, label: 'Digital' } : null,
    r.physicalRelease ? { date: r.physicalRelease, label: 'Physical' } : null,
  ].filter((d) => d !== null)
  dates.sort((a, b) => a.date.localeCompare(b.date))
  return dates[0] ?? null
}

/** Wanted → Missing and Wanted → Cutoff unmet (the route decides), for movies and episodes. */
export default function WantedPage() {
  const kind: WantedKind = useLocation().pathname.endsWith('/cutoff') ? 'cutoff' : 'missing'
  const [url, setUrl] = useSearchParams()
  const admin = useIsAdmin()
  const queryClient = useQueryClient()
  useUiFormats()

  const filter: WantedFilter = {
    type: url.get('type') === 'episodes' ? 'episodes' : 'movies',
    page: Math.max(1, Number(url.get('page')) || 1),
    pageSize: pageSizes.includes(Number(url.get('pageSize'))) ? Number(url.get('pageSize')) : 25,
    sortKey: sortKeys.find((k) => k === url.get('sort')) ?? (kind === 'missing' ? 'date' : 'title'),
    sortDirection: directions.find((d) => d === url.get('dir')) ?? (kind === 'missing' ? 'descending' : 'ascending'),
    monitored: url.get('unmonitored') !== '1',
    available: url.get('unreleased') !== '1',
  }
  const filterKey = JSON.stringify({ kind, ...filter })

  const update = (changes: Record<string, string | undefined>, keepPage = false) => {
    const next = new URLSearchParams(url)
    for (const [key, value] of Object.entries(changes)) {
      if (value) next.set(key, value)
      else next.delete(key)
    }
    if (!keepPage && !('page' in changes)) next.delete('page')
    setUrl(next)
  }

  const list = useQuery({
    queryKey: wantedKeys.list(kind, filter),
    queryFn: () => wantedApi.list(kind, filter),
    placeholderData: (previous) => previous,
  })
  const data = list.data
  const records = data?.records ?? []

  // The selection belongs to one list, page and sort order; it is dropped when any of them changes.
  const [selection, setSelection] = useState<{ key: string; ids: Set<number> }>({ key: filterKey, ids: new Set() })
  const selected = selection.key === filterKey ? selection.ids : new Set<number>()
  const setSelected = (ids: Set<number>) => setSelection({ key: filterKey, ids })
  const toggle = (id: number) => {
    const next = new Set(selected)
    if (next.has(id)) next.delete(id)
    else next.add(id)
    setSelected(next)
  }
  const allOnPage = records.length > 0 && records.every((r) => selected.has(r.id))

  const { toasts, push, dismiss } = useToasts()
  const [confirmAll, setConfirmAll] = useState(false)
  const [searchedIds, setSearchedIds] = useState<Set<number>>(new Set())

  const ids = (picked: number[]) => (filter.type === 'movies' ? { movieIds: picked } : { episodeIds: picked })

  const search = useMutation({
    mutationFn: (picked: number[]) => wantedApi.search({ kind, ...ids(picked) }),
    onSuccess: (result, picked) => {
      setSearchedIds(new Set([...searchedIds, ...picked]))
      push(
        'ok',
        result.queued > 0 ? 'Search queued' : 'Already queued',
        `${picked.length} ${noun(filter.type, picked.length)} searched in the background. Grabs show in Activity.`,
      )
      setSelected(new Set())
    },
    onError: (e) => push('error', 'Could not start the search', e.message),
  })
  const searchAll = useMutation({
    mutationFn: () => wantedApi.search({ kind, type: filter.type, all: true }),
    onSuccess: (result) => {
      setConfirmAll(false)
      push('ok', result.queued > 0 ? 'Search queued' : 'Already queued', 'Searching in the background. Grabs show in Activity.')
    },
    onError: (e) => push('error', 'Could not start the search', e.message),
  })
  const unmonitor = useMutation({
    mutationFn: (picked: number[]) =>
      wantedApi.setMonitored(filter.type === 'movies' ? picked : [], filter.type === 'episodes' ? picked : [], false),
    onSuccess: (result) => {
      push('ok', 'Unmonitored', `${result.changed} ${noun(filter.type, result.changed)} no longer searched automatically.`)
      setSelected(new Set())
    },
    onError: (e) => push('error', 'Could not unmonitor', e.message),
    onSettled: () => invalidateWanted(queryClient),
  })

  const text = texts[kind]
  const total = data?.totalRecords ?? 0
  const pages = data ? Math.max(1, Math.ceil(data.totalRecords / data.pageSize)) : 1
  const counts = data?.counts

  const sortHeader = (key: WantedSortKey, label: string, className?: string) => {
    const active = filter.sortKey === key
    return (
      <th className={className} aria-sort={active ? filter.sortDirection : 'none'}>
        <button
          type="button"
          className={`${styles.sort} ${active ? styles.sortActive : ''}`}
          onClick={() =>
            update({
              sort: key,
              dir: active ? (filter.sortDirection === 'ascending' ? 'descending' : 'ascending') : key === 'title' || key === 'profile' ? 'ascending' : 'descending',
            })
          }
        >
          {label}
          <span className={styles.sortArrow} aria-hidden="true">
            {active ? (filter.sortDirection === 'ascending' ? '↑' : '↓') : ''}
          </span>
        </button>
      </th>
    )
  }

  return (
    <div className={styles.wanted}>
      <PageHeader title={text.title} lead={text.lead} />

      <div className={styles.body}>
        <div className={styles.toolbar}>
          <div className={ui.seg} role="group" aria-label="Movies or episodes">
            {(['movies', 'episodes'] as const).map((type) => (
              <button key={type} type="button" aria-pressed={filter.type === type} onClick={() => update({ type: type === 'movies' ? undefined : type })}>
                <Icon name={type === 'movies' ? 'film' : 'tv'} size={14} />
                {type === 'movies' ? 'Movies' : 'Episodes'}
                {counts && <span className={styles.count}>{type === 'movies' ? counts.movies : counts.episodes}</span>}
              </button>
            ))}
          </div>
          <div className={styles.filters}>
            <label className={ui.check}>
              <input type="checkbox" checked={!filter.monitored} onChange={(e) => update({ unmonitored: e.target.checked ? '1' : undefined })} />
              Show unmonitored
            </label>
            {kind === 'missing' && (
              <label className={ui.check}>
                <input type="checkbox" checked={!filter.available} onChange={(e) => update({ unreleased: e.target.checked ? '1' : undefined })} />
                {filter.type === 'movies' ? 'Show not yet available' : 'Show unaired'}
              </label>
            )}
          </div>
          {admin && (
            <div className={styles.actions}>
              <button
                type="button"
                className={ui.primary}
                onClick={() => setConfirmAll(true)}
                disabled={total === 0 || searchAll.isPending}
                title={text.searchAll}
              >
                <Icon name="search" size={15} /> {text.searchShort}
              </button>
            </div>
          )}
        </div>

        {admin && selected.size > 0 && (
          <div className={styles.bulkbar} role="region" aria-label="Selection">
            <b>{`${selected.size} selected`}</b>
            <button type="button" className={`${ui.button} ${ui.sm}`} onClick={() => search.mutate([...selected])} disabled={search.isPending}>
              <Icon name="search" size={14} /> Search
            </button>
            <button type="button" className={`${ui.button} ${ui.sm}`} onClick={() => unmonitor.mutate([...selected])} disabled={unmonitor.isPending}>
              <Icon name="eyeoff" size={14} /> Unmonitor
            </button>
            <button type="button" className={`${ui.ghost} ${ui.sm} ${styles.bulkClear}`} onClick={() => setSelected(new Set())}>
              Clear
            </button>
          </div>
        )}

        {list.isError ? (
          <div className={`${ui.panel} ${ui.empty}`}>
            <Icon name="alert" size={26} className={styles.emptyErr} />
            <h3>The list could not be loaded</h3>
            <p>{list.error.message}</p>
          </div>
        ) : !data ? (
          <div className={`${ui.panel} ${styles.wait}`}>
            <Skeleton rows={5} />
          </div>
        ) : records.length === 0 ? (
          <div className={`${ui.panel} ${ui.empty}`}>
            <Icon name="check" size={26} className={styles.emptyOk} />
            <h3>{kind === 'missing' ? `No missing ${filter.type}` : `Every ${filter.type === 'movies' ? 'movie' : 'episode'} meets its profile`}</h3>
            <p>
              {kind === 'missing'
                ? filter.type === 'movies'
                  ? 'Every monitored movie that is released has a file. Movies count as released by the minimum availability of Settings → Indexers.'
                  : 'Every monitored episode that has aired has a file.'
                : 'No file is below the cutoff of its quality profile or in the wrong language.'}
            </p>
            {kind === 'missing' && filter.type === 'movies' && (
              <div className={styles.emptyActions}>
                <Link className={ui.button} to="/settings/indexers">
                  Minimum availability
                </Link>
              </div>
            )}
          </div>
        ) : (
          <>
            <div className={`${styles.tableWrap} ${list.isPlaceholderData ? styles.stale : ''}`}>
              <table className={styles.table}>
                <thead>
                  <tr>
                    {admin && (
                      <th className={styles.selectCell}>
                        <input
                          type="checkbox"
                          aria-label="Select all on this page"
                          checked={allOnPage}
                          onChange={() => setSelected(allOnPage ? new Set() : new Set(records.map((r) => r.id)))}
                        />
                      </th>
                    )}
                    {sortHeader('title', filter.type === 'movies' ? 'Movie' : 'Series')}
                    {filter.type === 'episodes' && <th>Episode</th>}
                    {sortHeader('date', filter.type === 'movies' ? 'In cinemas' : 'Aired')}
                    {filter.type === 'movies' && kind === 'missing' && <th className={styles.wide}>Home release</th>}
                    {kind === 'cutoff' && sortHeader('quality', 'Quality')}
                    {kind === 'cutoff' && <th>Cutoff</th>}
                    {sortHeader('profile', 'Profile', styles.wide)}
                    {sortHeader('status', 'Status')}
                    {sortHeader('lastSearched', 'Last searched', styles.wide)}
                    {admin && <th className={styles.actionCell}><span className={styles.srOnly}>Actions</span></th>}
                  </tr>
                </thead>
                <tbody>
                  {records.map((r) => {
                    const home = homeRelease(r)
                    return (
                      <tr key={r.id} className={`${selected.has(r.id) ? styles.selected : ''} ${r.monitored ? '' : styles.unmonitored}`}>
                        {admin && (
                          <td className={styles.selectCell}>
                            <input type="checkbox" aria-label={`Select ${r.title}`} checked={selected.has(r.id)} onChange={() => toggle(r.id)} />
                          </td>
                        )}
                        <td className={styles.titleCell}>
                          <div className={styles.titleBox}>
                            <Thumb record={r} />
                            <div className={styles.titleText}>
                              <Link to={detailLink(r)} className={styles.titleLink}>
                                {r.title}
                              </Link>
                              {r.type === 'movie' && r.year > 0 && <span className={`${styles.faint} ${styles.nowrap}`}> ({r.year})</span>}
                              {!r.monitored && <span className={styles.flag}>Unmonitored</span>}
                            </div>
                          </div>
                        </td>
                        {filter.type === 'episodes' && (
                          <td className={styles.episodeCell} data-label="Episode">
                            <span className={styles.mono}>{episodeNumber(r)}</span> <span className={styles.episodeTitle}>{r.episodeTitle}</span>
                          </td>
                        )}
                        <td className={styles.nowrap} data-label={filter.type === 'movies' ? 'In cinemas' : 'Aired'} title={r.date ?? undefined}>
                          {r.date ? formatDate(r.date) : <span className={styles.faint}>Unknown</span>}
                        </td>
                        {filter.type === 'movies' && kind === 'missing' && (
                          <td className={`${styles.wide} ${styles.nowrap}`} data-label="Home release" title={[r.digitalRelease && `Digital ${r.digitalRelease}`, r.physicalRelease && `Physical ${r.physicalRelease}`].filter(Boolean).join(', ') || undefined}>
                            {home ? (
                              <>
                                {formatDate(home.date)} <span className={styles.faint}>{home.label}</span>
                              </>
                            ) : (
                              <span className={styles.faint}>–</span>
                            )}
                          </td>
                        )}
                        {kind === 'cutoff' && (
                          <td className={styles.nowrap} data-label="Quality">
                            <span className={styles.quality}>{r.quality ?? 'Unknown'}</span>
                            {r.languages.length > 0 && <span className={styles.faint}> {r.languages.join(', ')}</span>}
                          </td>
                        )}
                        {kind === 'cutoff' && (
                          <td className={styles.nowrap} data-label="Cutoff">
                            {r.cutoff}
                            {r.wantedLanguage && <span className={styles.faint}> {r.wantedLanguage}</span>}
                          </td>
                        )}
                        <td className={`${styles.wide} ${styles.nowrap}`} data-label="Profile">
                          {r.profile}
                        </td>
                        <td data-label="Status">
                          <Status record={r} />
                        </td>
                        <td className={`${styles.wide} ${styles.nowrap}`} data-label="Last searched" title={r.lastSearched ? formatDateTime(r.lastSearched) : undefined}>
                          {r.lastSearched ? relativeTime(r.lastSearched) : <span className={styles.faint}>Never</span>}
                        </td>
                        {admin && (
                          <td className={styles.actionCell}>
                            <button
                              type="button"
                              className={styles.iconAction}
                              onClick={() => search.mutate([r.id])}
                              disabled={search.isPending}
                              title={searchedIds.has(r.id) ? 'Search queued' : 'Search'}
                              aria-label={`Search ${r.type === 'movie' ? r.title : `${r.title} ${episodeNumber(r)}`}`}
                            >
                              <Icon name={searchedIds.has(r.id) ? 'check' : 'search'} size={15} />
                            </button>
                          </td>
                        )}
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>

            <nav className={styles.pager} aria-label="Pages">
              <span className={styles.pagerText}>
                Page <strong>{data.page}</strong> of {pages} · {new Intl.NumberFormat().format(total)} {noun(filter.type, total)}
              </span>
              <div className={styles.pagerControls}>
                <label className={styles.pageSize}>
                  <span className={styles.srOnly}>Rows per page</span>
                  <select className={ui.select} value={filter.pageSize} onChange={(e) => update({ pageSize: e.target.value === '25' ? undefined : e.target.value })}>
                    {pageSizes.map((size) => (
                      <option key={size} value={size}>
                        {size} per page
                      </option>
                    ))}
                  </select>
                </label>
                <button
                  className={`${ui.button} ${ui.icon}`}
                  type="button"
                  disabled={data.page <= 1}
                  onClick={() => update({ page: data.page > 2 ? String(data.page - 1) : undefined })}
                  aria-label="Previous page"
                  title="Previous page"
                >
                  <Icon name="left" size={16} />
                </button>
                <button
                  className={`${ui.button} ${ui.icon}`}
                  type="button"
                  disabled={data.page >= pages}
                  onClick={() => update({ page: String(data.page + 1) })}
                  aria-label="Next page"
                  title="Next page"
                >
                  <Icon name="right" size={16} />
                </button>
              </div>
            </nav>
          </>
        )}
      </div>

      {confirmAll && (
        <Modal title={text.searchAll} onClose={() => setConfirmAll(false)}>
          <p className={styles.dialogText}>
            {kind === 'missing'
              ? `All ${total} missing ${noun(filter.type, total)} on the list are searched one by one. `
              : `All ${total} ${noun(filter.type, total)} below the cutoff are searched for an upgrade. `}
            Every search costs one request per indexer, so a long list can use up the daily limit of an indexer.
          </p>
          <div className={styles.dialogActions}>
            <button type="button" className={ui.button} onClick={() => setConfirmAll(false)}>
              Cancel
            </button>
            <button type="button" className={ui.primary} onClick={() => searchAll.mutate()} disabled={searchAll.isPending}>
              {searchAll.isPending ? 'Starting…' : `Search ${total} ${noun(filter.type, total)}`}
            </button>
          </div>
        </Modal>
      )}
      <Toasts toasts={toasts} onDismiss={dismiss} />
    </div>
  )
}

/** Small poster in the first column; a tinted box with an icon when there is none. */
function Thumb({ record: r }: { record: WantedRecord }) {
  const [failed, setFailed] = useState(false)
  const url = tmdbImage(r.posterPath, 'w92')
  return (
    <span className={styles.thumb} data-type={r.type} aria-hidden="true">
      {url && !failed ? <img src={url} alt="" loading="lazy" onError={() => setFailed(true)} /> : <Icon name={r.type === 'movie' ? 'film' : 'tv'} size={14} />}
    </span>
  )
}

function Status({ record: r }: { record: WantedRecord }) {
  if (r.queue) {
    return <span className={`${ui.badge} ${styles.queued}`}>{queueLabels[r.queue]}</span>
  }
  switch (r.status) {
    case 'missing':
      return <span className={`${ui.badge} ${ui.missing}`}>Missing</span>
    case 'notAvailable':
      return (
        <span className={`${ui.badge} ${ui.idle}`} title="Not available yet by the minimum availability (Settings → Indexers)">
          {r.availableFrom ? `From ${formatDate(r.availableFrom)}` : 'Not available'}
        </span>
      )
    case 'unaired':
      return <span className={`${ui.badge} ${ui.idle}`}>{r.date ? 'Unaired' : 'Air date unknown'}</span>
    default:
      return (
        <span className={styles.reasons}>
          {r.reasons.map((reason) => (
            <span key={reason} className={`${ui.badge} ${ui.waiting}`}>
              {reasonLabels[reason]}
            </span>
          ))}
        </span>
      )
  }
}
