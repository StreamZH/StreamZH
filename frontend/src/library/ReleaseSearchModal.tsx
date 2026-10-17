import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { createPortal } from 'react-dom'
import { Link } from 'react-router'
import { historyKeys } from '../api/history'
import { episodeLabel, formatAge, releaseKeys, releasesApi, type Release, type SearchTarget } from '../api/releases'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import { formatBytes } from '../lib/format'
import styles from './ReleaseSearch.module.css'

type SortKey = 'rank' | 'title' | 'indexer' | 'age' | 'size' | 'grabs'

const sortLabels: Record<SortKey, string> = {
  rank: 'Best match',
  title: 'Title',
  indexer: 'Indexer',
  age: 'Age',
  size: 'Size',
  grabs: 'Grabs',
}

/** Direction a column sorts in on the first click: newest, largest and most grabbed first. */
const firstDescending: Record<SortKey, boolean> = { rank: false, title: false, indexer: false, age: false, size: true, grabs: true }

function compare(a: Release, b: Release, key: SortKey): number {
  switch (key) {
    case 'rank':
      return a.rank - b.rank
    case 'title':
      return a.title.localeCompare(b.title)
    case 'indexer':
      return a.indexer.localeCompare(b.indexer) || a.rank - b.rank
    case 'age':
      return a.ageHours - b.ageHours
    case 'size':
      return a.size - b.size
    case 'grabs':
      return (a.grabs ?? -1) - (b.grabs ?? -1)
  }
}

/** Manual search: every release the indexers return, with StreamZH's verdict, and a grab button per row. */
export default function ReleaseSearchModal({ title, target, onClose }: { title: string; target: SearchTarget; onClose: () => void }) {
  const indexers = useQuery({ queryKey: releaseKeys.indexers, queryFn: releasesApi.activeIndexers, staleTime: 60_000 })
  const search = useQuery({
    queryKey: releaseKeys.search(target),
    queryFn: () => releasesApi.search(target),
    retry: false,
    staleTime: Infinity,
    gcTime: 60_000,
  })
  const [showRejected, setShowRejected] = useState(true)
  const [sort, setSort] = useState<{ key: SortKey; desc: boolean }>({ key: 'rank', desc: false })

  const releases = useMemo(() => {
    const all = search.data?.releases ?? []
    const shown = showRejected ? all : all.filter((r) => r.accepted)
    return [...shown].sort((a, b) => (sort.desc ? -1 : 1) * compare(a, b, sort.key))
  }, [search.data, showRejected, sort])

  const sortBy = (key: SortKey) =>
    setSort((current) => (current.key === key ? { key, desc: !current.desc } : { key, desc: firstDescending[key] }))

  const data = search.data
  const accepted = data?.releases.filter((r) => r.accepted).length ?? 0
  const rejected = (data?.releases.length ?? 0) - accepted
  const indexerCount = indexers.data?.interactiveSearch

  // Portal: the dialog must not inherit styles of the place it is opened from (hero actions, episode table).
  return createPortal(
    <Modal title={title} onClose={onClose}>
      <div className={styles.root}>
        {search.isFetching ? (
          <div className={styles.state} role="status">
            <span className={styles.spinner} aria-hidden="true" />
            <span>{indexerCount ? `Searching ${indexerCount} indexer${indexerCount === 1 ? '' : 's'}…` : 'Searching indexers…'}</span>
            <span className={ui.muted}>This can take up to a minute when an indexer is slow.</span>
          </div>
        ) : search.isError ? (
          <div className={styles.state}>
            <p className={ui.error}>{search.error.message}</p>
            <button className={ui.button} type="button" onClick={() => void search.refetch()}>
              Try again
            </button>
          </div>
        ) : data && data.indexers === 0 ? (
          <div className={styles.state}>
            <p>No indexer is enabled for manual search.</p>
            <p className={ui.muted}>StreamZH searches Usenet indexers (Newznab) for releases. Add one, or check that manual search is switched on for it.</p>
            <Link className={ui.primary} to="/indexers">
              Set up indexers
            </Link>
          </div>
        ) : data ? (
          <>
            <div className={styles.toolbar}>
              <p className={styles.summary}>
                <strong>{data.releases.length}</strong> release{data.releases.length === 1 ? '' : 's'} from {data.indexers} indexer
                {data.indexers === 1 ? '' : 's'} · <span className={styles.okText}>{accepted} accepted</span>
              </p>
              <div className={styles.tools}>
                <label className={ui.check}>
                  <input type="checkbox" checked={showRejected} onChange={(e) => setShowRejected(e.target.checked)} />
                  <span>Show rejected ({rejected})</span>
                </label>
                <select
                  className={`${ui.select} ${styles.sortSelect}`}
                  aria-label="Sort by"
                  value={`${sort.key}:${sort.desc ? 'desc' : 'asc'}`}
                  onChange={(e) => {
                    const [key, dir] = e.target.value.split(':') as [SortKey, string]
                    setSort({ key, desc: dir === 'desc' })
                  }}
                >
                  <option value="rank:asc">Best match</option>
                  <option value="age:asc">Newest</option>
                  <option value="size:desc">Largest</option>
                  <option value="size:asc">Smallest</option>
                  <option value="grabs:desc">Most grabbed</option>
                  <option value="indexer:asc">Indexer</option>
                  <option value="title:asc">Title</option>
                </select>
                <button className={ui.button} type="button" onClick={() => void search.refetch()}>
                  Search again
                </button>
              </div>
            </div>

            {data.failedIndexers.length > 0 && (
              <p className={styles.warning}>
                {data.failedIndexers.map((f) => (
                  <span key={f.indexerId}>
                    <strong>{f.indexer}</strong> failed: {f.message}
                  </span>
                ))}
              </p>
            )}

            {releases.length === 0 ? (
              <div className={styles.state}>
                <p>{data.releases.length === 0 ? 'No releases found.' : 'No accepted releases.'}</p>
                <p className={ui.muted}>
                  {data.releases.length === 0
                    ? 'The indexers have nothing for this title yet. Try again later.'
                    : `${rejected} release${rejected === 1 ? ' was' : 's were'} rejected. Turn on "Show rejected" to see why.`}
                </p>
              </div>
            ) : (
              <div className={styles.table} role="table" aria-label="Releases">
                <div className={styles.head} role="row">
                  <span role="columnheader" aria-label="Status" />
                  {(['title', 'indexer', 'age', 'size'] as SortKey[]).map((key) => (
                    <SortHeader key={key} sortKey={key} sort={sort} onSort={sortBy} />
                  ))}
                  <span role="columnheader">Quality</span>
                  <span role="columnheader">Languages</span>
                  <SortHeader sortKey="grabs" sort={sort} onSort={sortBy} />
                  <span role="columnheader" className={styles.headAction}>
                    {sort.key !== 'rank' && (
                      <button type="button" className={styles.reset} onClick={() => setSort({ key: 'rank', desc: false })}>
                        Best match
                      </button>
                    )}
                  </span>
                </div>
                {releases.map((release) => (
                  <ReleaseRow key={`${release.indexerId}:${release.guid}`} release={release} />
                ))}
              </div>
            )}
          </>
        ) : null}
      </div>
    </Modal>,
    document.body,
  )
}

function SortHeader({ sortKey, sort, onSort }: { sortKey: SortKey; sort: { key: SortKey; desc: boolean }; onSort: (key: SortKey) => void }) {
  const active = sort.key === sortKey
  return (
    <span role="columnheader" aria-sort={active ? (sort.desc ? 'descending' : 'ascending') : 'none'}>
      <button type="button" className={`${styles.sort} ${active ? styles.sortActive : ''}`} onClick={() => onSort(sortKey)}>
        {sortLabels[sortKey]}
        {active && <span aria-hidden="true">{sort.desc ? ' ↓' : ' ↑'}</span>}
      </button>
    </span>
  )
}

function ReleaseRow({ release }: { release: Release }) {
  const queryClient = useQueryClient()
  const [reasonsOpen, setReasonsOpen] = useState(false)
  const [confirming, setConfirming] = useState(false)
  const grab = useMutation({
    mutationFn: () => releasesApi.grab(release.indexerId, release.guid),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: historyKeys.all }),
  })
  const episodes = episodeLabel(release)
  const reasons = release.rejections.map((r) => r.message).join('\n')

  return (
    <div className={`${styles.row} ${release.accepted ? '' : styles.rejectedRow}`} role="row">
      <span role="cell" className={styles.status}>
        <button
          type="button"
          className={`${styles.statusIcon} ${release.accepted ? styles.statusOk : styles.statusRejected}`}
          title={release.accepted ? 'Accepted' : reasons}
          aria-label={release.accepted ? 'Accepted' : `Rejected: ${reasons}`}
          aria-expanded={release.accepted ? undefined : reasonsOpen}
          onClick={() => !release.accepted && setReasonsOpen((open) => !open)}
        >
          {release.accepted ? '✓' : '!'}
        </button>
      </span>

      <span role="cell" className={styles.title}>
        {release.infoUrl ? (
          <a href={release.infoUrl} target="_blank" rel="noreferrer">
            {release.title}
          </a>
        ) : (
          release.title
        )}
      </span>

      <span className={styles.meta}>
        <span role="cell" className={styles.cell}>
          {release.indexer}
        </span>
        <span role="cell" className={styles.cell} title={new Date(release.publishDate).toLocaleString()}>
          {formatAge(release)}
        </span>
        <span role="cell" className={`${styles.cell} ${styles.num}`}>
          {formatBytes(release.size)}
        </span>
        <span role="cell" className={styles.cell}>
          {episodes && <span className={styles.episodes}>{episodes}</span>}
          <span className={styles.quality}>{release.quality}</span>
        </span>
        <span role="cell" className={styles.cell}>
          {release.languages.length > 0 ? release.languages.join(', ') : <span className={ui.muted}>–</span>}
        </span>
        <span role="cell" className={`${styles.cell} ${styles.num}`} title="Grabs on the indexer">
          <span className={styles.mobileLabel}>Grabs </span>
          {release.grabs ?? '–'}
        </span>
      </span>

      <span role="cell" className={styles.action}>
        {grab.isSuccess ? (
          <span className={styles.grabbed}>Grabbed</span>
        ) : confirming ? (
          <span className={styles.confirm}>
            <button
              className={ui.primary}
              type="button"
              onClick={() => {
                setConfirming(false)
                grab.mutate()
              }}
            >
              Grab anyway
            </button>
            <button className={ui.button} type="button" onClick={() => setConfirming(false)}>
              Cancel
            </button>
          </span>
        ) : (
          <button
            className={release.accepted ? ui.primary : ui.button}
            type="button"
            disabled={grab.isPending}
            onClick={() => (release.accepted ? grab.mutate() : setConfirming(true))}
          >
            {grab.isPending ? 'Grabbing…' : 'Grab'}
          </button>
        )}
      </span>

      {(reasonsOpen || confirming) && !release.accepted && (
        <ul className={styles.reasons}>
          {release.rejections.map((r) => (
            <li key={r.code + r.message}>{r.message}</li>
          ))}
        </ul>
      )}
      {grab.isSuccess && (
        <p className={styles.rowNote}>
          {grab.data.folder.replaceAll('\\', '/').endsWith('nzb-queue') ? (
            <>
              Added to the download queue. Follow it under <a href="/activity">Activity</a>.
            </>
          ) : (
            <>
              Saved as <code>{grab.data.fileName}</code> in the NZB folder.
            </>
          )}
        </p>
      )}
      {grab.error && <p className={`${ui.error} ${styles.rowNote}`}>{grab.error.message}</p>}
    </div>
  )
}
