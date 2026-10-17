import { useMutation, useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query'
import { useCallback, useMemo, useState, type FormEvent } from 'react'
import { Link, useSearchParams } from 'react-router'
import { downloadSettingsApi, downloadSettingsKeys } from '../api/downloadSettings'
import { historyKeys } from '../api/history'
import { indexerActivityKeys } from '../api/indexerActivity'
import { indexerKeys, indexersApi } from '../api/indexers'
import {
  categoryName,
  formatAgeHours,
  newznabCategories,
  searchApi,
  searchKeys,
  type FreeRelease,
  type FreeSearch,
  type SearchParams,
  type SearchType,
} from '../api/search'
import ui from '../components/ui.module.css'
import page from '../pages/Page.module.css'
import { formatBytes, formatDateTime } from '../lib/format'
import MultiSelect, { type MultiOption } from './MultiSelect'
import Toasts, { type ToastMessage } from './Toast'
import styles from './IndexerTools.module.css'

type SortKey = 'indexer' | 'title' | 'age' | 'size' | 'grabs' | 'quality'

const sortLabels: Record<SortKey, string> = {
  indexer: 'Indexer',
  title: 'Title',
  age: 'Age',
  size: 'Size',
  grabs: 'Grabs',
  quality: 'Quality',
}

/** First click: newest, largest, most grabbed and best quality first. */
const firstDescending: Record<SortKey, boolean> = { indexer: false, title: false, age: false, size: true, grabs: true, quality: true }

const typeLabels: Record<SearchType, string> = { search: 'All', movie: 'Movies', tv: 'TV' }

const pageStep = 100

const categoryOptions: MultiOption[] = newznabCategories.flatMap((group) => [
  { value: group.id, label: group.name, hint: String(group.id) },
  ...group.subs.map((sub) => ({ value: sub.id, label: sub.name, parent: group.id, hint: String(sub.id) })),
])

/** "WEBDL-2160p" → 2160, so quality sorts by resolution first. */
function qualityRank(quality: string): number {
  const match = /(\d{3,4})p/i.exec(quality)
  return match ? Number(match[1]) : quality === 'Unknown' ? -1 : 0
}

function compare(a: FreeRelease, b: FreeRelease, key: SortKey): number {
  switch (key) {
    case 'indexer':
      return a.indexer.localeCompare(b.indexer) || a.ageHours - b.ageHours
    case 'title':
      return a.title.localeCompare(b.title)
    case 'age':
      return a.ageHours - b.ageHours
    case 'size':
      return a.size - b.size
    case 'grabs':
      return (a.grabs ?? -1) - (b.grabs ?? -1)
    case 'quality':
      return qualityRank(a.quality) - qualityRank(b.quality) || a.quality.localeCompare(b.quality)
  }
}

function idList(value: string | null): number[] {
  return (value ?? '')
    .split(',')
    .map((v) => Number(v))
    .filter((v) => Number.isInteger(v) && v > 0)
}

/** The submitted search lives in the URL (?q=&type=&cat=&ids=), so it survives a reload and the back button. */
function paramsFromUrl(url: URLSearchParams): SearchParams | null {
  if (!url.has('q')) return null
  const type = url.get('type')
  return {
    query: url.get('q') ?? '',
    type: type === 'movie' || type === 'tv' ? type : 'search',
    categories: idList(url.get('cat')),
    indexerIds: idList(url.get('ids')),
  }
}

const releaseKey = (r: Pick<FreeRelease, 'indexerId' | 'guid'>) => `${r.indexerId}:${r.guid}`

/** Search every indexer by free text, without a library item (like Prowlarr's Search page). */
export default function SearchPage() {
  const [url, setUrl] = useSearchParams()
  const submitted = useMemo(() => paramsFromUrl(url), [url])

  const indexers = useQuery({ queryKey: indexerKeys.list, queryFn: indexersApi.list })
  const searchable = useMemo(() => (indexers.data ?? []).filter((i) => i.enableInteractiveSearch), [indexers.data])

  const search = useQuery({
    queryKey: searchKeys.search(submitted ?? { query: '', type: 'search', categories: [], indexerIds: [] }),
    queryFn: () => searchApi.search(submitted!),
    enabled: submitted != null,
    retry: false,
    staleTime: Infinity,
    gcTime: 5 * 60_000,
  })

  const run = (params: SearchParams) => {
    const next = new URLSearchParams({ q: params.query, type: params.type })
    if (params.categories.length > 0) next.set('cat', params.categories.join(','))
    if (params.indexerIds.length > 0) next.set('ids', params.indexerIds.join(','))
    if (next.toString() === url.toString()) {
      void search.refetch()
    } else {
      setUrl(next)
    }
  }

  const askedCount = submitted && submitted.indexerIds.length > 0 ? submitted.indexerIds.length : searchable.length

  return (
    <div className={`${page.page} ${styles.wide}`}>
      <header>
        <p className={styles.eyebrow}>Indexers</p>
        <h1 className={page.title}>Search</h1>
        <p className={page.lead}>Search all your indexers at once, without adding anything to the library.</p>
      </header>

      {/* Keyed by the URL: back and forward put the submitted search back into the form. */}
      <SearchForm
        key={url.toString()}
        initial={submitted}
        busy={search.isFetching}
        indexerOptions={searchable.map((i) => ({ value: i.id, label: i.name, hint: i.status && !i.status.healthy ? 'failing' : undefined }))}
        indexersLoading={indexers.isLoading}
        onSearch={run}
      />

      <Results search={search} submitted={submitted} askedCount={askedCount} />
    </div>
  )
}

function SearchForm({
  initial,
  busy,
  indexerOptions,
  indexersLoading,
  onSearch,
}: {
  initial: SearchParams | null
  busy: boolean
  indexerOptions: MultiOption[]
  indexersLoading: boolean
  onSearch: (params: SearchParams) => void
}) {
  const [query, setQuery] = useState(initial?.query ?? '')
  const [type, setType] = useState<SearchType>(initial?.type ?? 'search')
  const [categories, setCategories] = useState<number[]>(initial?.categories ?? [])
  const [indexerIds, setIndexerIds] = useState<number[]>(initial?.indexerIds ?? [])

  const submit = (event: FormEvent) => {
    event.preventDefault()
    onSearch({ query: query.trim(), type, categories, indexerIds })
  }

  return (
    <form className={`${page.panel} ${styles.searchForm}`} onSubmit={submit} role="search">
      <div className={styles.searchRow}>
        <label className={styles.queryField}>
          <span className={styles.srOnly}>Search text</span>
          <svg className={styles.queryIcon} width="16" height="16" viewBox="0 0 24 24" aria-hidden="true">
            <circle cx="11" cy="11" r="7" fill="none" stroke="currentColor" strokeWidth="1.75" />
            <path d="m20 20-3.5-3.5" fill="none" stroke="currentColor" strokeWidth="1.75" strokeLinecap="round" />
          </svg>
          <input
            className={`${ui.input} ${styles.queryInput}`}
            type="search"
            value={query}
            maxLength={200}
            placeholder="Title, release name, group…"
            autoFocus
            onChange={(e) => setQuery(e.target.value)}
          />
        </label>
        <div className={styles.segmented} role="radiogroup" aria-label="Search type">
          {(Object.keys(typeLabels) as SearchType[]).map((t) => (
            <button
              key={t}
              type="button"
              role="radio"
              aria-checked={type === t}
              className={type === t ? styles.segmentActive : styles.segment}
              onClick={() => setType(t)}
            >
              {typeLabels[t]}
            </button>
          ))}
        </div>
        <button className={`${ui.primary} ${styles.searchButton}`} type="submit" disabled={busy}>
          {busy ? 'Searching…' : 'Search'}
        </button>
      </div>
      <div className={styles.filterRow}>
        <MultiSelect
          label="Categories"
          placeholder={type === 'movie' ? 'Movie categories of each indexer' : type === 'tv' ? 'TV categories of each indexer' : 'All categories'}
          options={categoryOptions}
          selected={categories}
          onChange={setCategories}
        />
        <MultiSelect
          label="Indexers"
          placeholder={indexersLoading ? 'Loading…' : `All indexers (${indexerOptions.length})`}
          options={indexerOptions}
          selected={indexerIds}
          onChange={setIndexerIds}
        />
        <p className={styles.formHint}>Leave the text empty to list the newest releases.</p>
      </div>
    </form>
  )
}

function Results({
  search,
  submitted,
  askedCount,
}: {
  search: UseQueryResult<FreeSearch, Error>
  submitted: SearchParams | null
  askedCount: number
}) {
  const queryClient = useQueryClient()
  const [filter, setFilter] = useState('')
  const [sort, setSort] = useState<{ key: SortKey; desc: boolean }>({ key: 'age', desc: false })
  const [limit, setLimit] = useState(pageStep)
  const [toasts, setToasts] = useState<ToastMessage[]>([])
  const [grabbed, setGrabbed] = useState<Record<string, string>>({})
  const [category, setCategory] = useState('other')
  const categories = useQuery({ queryKey: downloadSettingsKeys.choices, queryFn: downloadSettingsApi.choices })

  const dismiss = useCallback((id: number) => setToasts((all) => all.filter((t) => t.id !== id)), [])
  const notify = (toast: Omit<ToastMessage, 'id'>) => setToasts((all) => [...all.slice(-3), { ...toast, id: Date.now() + Math.random() }])

  const grab = useMutation({
    mutationFn: (release: FreeRelease) => searchApi.grab(release.indexerId, release.guid, category),
    onSuccess: (result, release) => {
      setGrabbed((all) => ({ ...all, [releaseKey(release)]: result.fileName }))
      notify({ tone: 'ok', title: `Grabbed from ${result.indexer}`, detail: result.fileName })
      void queryClient.invalidateQueries({ queryKey: historyKeys.all })
      void queryClient.invalidateQueries({ queryKey: indexerActivityKeys.all })
    },
    onError: (error, release) => notify({ tone: 'error', title: `Could not grab from ${release.indexer}`, detail: error.message }),
  })

  const data = search.data
  const releases = useMemo(() => {
    const words = filter.toLowerCase().split(/\s+/).filter(Boolean)
    const all = data?.releases ?? []
    const shown =
      words.length === 0
        ? all
        : all.filter((r) => {
            const text = `${r.title} ${r.indexer} ${r.quality} ${r.languages.join(' ')}`.toLowerCase()
            return words.every((w) => text.includes(w))
          })
    return [...shown].sort((a, b) => (sort.desc ? -1 : 1) * compare(a, b, sort.key))
  }, [data, filter, sort])

  const sortBy = (key: SortKey) => {
    setSort((current) => (current.key === key ? { key, desc: !current.desc } : { key, desc: firstDescending[key] }))
    setLimit(pageStep)
  }

  const toastLayer = <Toasts toasts={toasts} onDismiss={dismiss} />

  if (!submitted) {
    return (
      <div className={styles.empty}>
        <p className={styles.emptyTitle}>Find any release</p>
        <p className={ui.muted}>
          Type a title or a release name and press Search. Narrow it down by type, category or indexer. Grabbed results go to the download queue
          (Activity).
        </p>
      </div>
    )
  }

  if (search.isFetching) {
    return (
      <div className={styles.state} role="status">
        <span className={styles.spinner} aria-hidden="true" />
        <span>{askedCount ? `Searching ${askedCount} indexer${askedCount === 1 ? '' : 's'}…` : 'Searching indexers…'}</span>
        <span className={ui.muted}>This can take up to a minute when an indexer is slow.</span>
      </div>
    )
  }

  if (search.isError) {
    return (
      <div className={styles.state}>
        <p className={ui.error}>{search.error.message}</p>
        <button className={ui.button} type="button" onClick={() => void search.refetch()}>
          Try again
        </button>
      </div>
    )
  }

  if (!data) return null

  if (data.indexers === 0) {
    return (
      <div className={styles.state}>
        <p>No indexer is enabled for manual search.</p>
        <p className={ui.muted}>Add a Usenet indexer, or check that manual search is switched on for the ones you chose.</p>
        <Link className={ui.primary} to="/indexers">
          Set up indexers
        </Link>
      </div>
    )
  }

  const visible = releases.slice(0, limit)

  return (
    <section className={styles.results} aria-label="Search results">
      <div className={styles.toolbar}>
        <p className={styles.summary}>
          <strong>{data.releases.length}</strong> release{data.releases.length === 1 ? '' : 's'} from <strong>{data.indexers}</strong> indexer
          {data.indexers === 1 ? '' : 's'}
          {filter && releases.length !== data.releases.length && <span className={ui.muted}> · {releases.length} match the filter</span>}
        </p>
        <div className={styles.tools}>
          <input
            className={`${ui.input} ${styles.filterInput}`}
            type="search"
            placeholder="Filter results"
            aria-label="Filter results"
            value={filter}
            onChange={(e) => {
              setFilter(e.target.value)
              setLimit(pageStep)
            }}
          />
          <select
            className={`${ui.select} ${styles.sortSelect}`}
            aria-label="Sort by"
            value={`${sort.key}:${sort.desc ? 'desc' : 'asc'}`}
            onChange={(e) => {
              const [key, dir] = e.target.value.split(':') as [SortKey, string]
              setSort({ key, desc: dir === 'desc' })
            }}
          >
            <option value="age:asc">Newest</option>
            <option value="size:desc">Largest</option>
            <option value="size:asc">Smallest</option>
            <option value="grabs:desc">Most grabbed</option>
            <option value="quality:desc">Best quality</option>
            <option value="indexer:asc">Indexer</option>
            <option value="title:asc">Title</option>
          </select>
          {categories.data && (
            <select
              className={`${ui.select} ${styles.sortSelect}`}
              aria-label="Download category for grabs"
              title="Download category for grabs"
              value={category}
              onChange={(e) => setCategory(e.target.value)}
            >
              {categories.data.map((c) => (
                <option key={c.name} value={c.name}>
                  Grab as {c.name}
                </option>
              ))}
            </select>
          )}
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
          <p>{data.releases.length === 0 ? 'No releases found.' : 'No release matches the filter.'}</p>
          <p className={ui.muted}>
            {data.releases.length === 0
              ? 'Try fewer words, another type, or fewer categories.'
              : `${data.releases.length} release${data.releases.length === 1 ? ' is' : 's are'} hidden by "${filter}".`}
          </p>
        </div>
      ) : (
        <div className={styles.releaseTable} role="table" aria-label="Releases">
          <div className={`${styles.releaseRow} ${styles.releaseHead}`} role="row">
            <SortHeader sortKey="indexer" sort={sort} onSort={sortBy} />
            <SortHeader sortKey="title" sort={sort} onSort={sortBy} />
            <SortHeader sortKey="age" sort={sort} onSort={sortBy} />
            <SortHeader sortKey="size" sort={sort} onSort={sortBy} />
            <span role="columnheader">Categories</span>
            <SortHeader sortKey="grabs" sort={sort} onSort={sortBy} />
            <SortHeader sortKey="quality" sort={sort} onSort={sortBy} />
            <span role="columnheader">Languages</span>
            <span role="columnheader" aria-label="Actions" />
          </div>
          {visible.map((release) => {
            const key = releaseKey(release)
            return (
              <ReleaseRow
                key={key}
                release={release}
                grabbedFile={grabbed[key]}
                grabbing={grab.isPending && grab.variables != null && releaseKey(grab.variables) === key}
                onGrab={() => grab.mutate(release)}
              />
            )
          })}
        </div>
      )}

      {releases.length > limit && (
        <div className={styles.more}>
          <span className={ui.muted}>
            Showing {limit} of {releases.length}
          </span>
          <button className={ui.button} type="button" onClick={() => setLimit((l) => l + pageStep)}>
            Show {Math.min(pageStep, releases.length - limit)} more
          </button>
        </div>
      )}
      {toastLayer}
    </section>
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

function ReleaseRow({
  release,
  grabbedFile,
  grabbing,
  onGrab,
}: {
  release: FreeRelease
  grabbedFile: string | undefined
  grabbing: boolean
  onGrab: () => void
}) {
  const categoryNames = release.categories.map(categoryName)
  return (
    <div className={styles.releaseRow} role="row">
      <span role="cell" className={styles.indexerCell}>
        {release.indexer}
      </span>
      <span role="cell" className={styles.titleCell}>
        {release.title}
      </span>
      <span className={styles.metaCells}>
        <span role="cell" className={`${styles.cell} ${styles.num}`} title={formatDateTime(release.publishDate)}>
          {formatAgeHours(release.ageHours)}
        </span>
        <span role="cell" className={`${styles.cell} ${styles.num}`}>
          {formatBytes(release.size)}
        </span>
        <span role="cell" className={styles.cell} title={categoryNames.join(', ')}>
          {categoryNames.length === 0 ? (
            <span className={ui.muted}>–</span>
          ) : (
            <span className={styles.chips}>
              {categoryNames.slice(0, 2).map((name, i) => (
                <span key={`${release.categories[i]}`} className={styles.chip}>
                  {name}
                </span>
              ))}
              {categoryNames.length > 2 && <span className={styles.chipMore}>+{categoryNames.length - 2}</span>}
            </span>
          )}
        </span>
        <span role="cell" className={`${styles.cell} ${styles.num}`} title="Grabs on the indexer">
          <span className={styles.mobileLabel}>Grabs </span>
          {release.grabs ?? '–'}
        </span>
        <span role="cell" className={`${styles.cell} ${styles.quality}`}>
          {release.quality === 'Unknown' ? <span className={ui.muted}>Unknown</span> : release.quality}
        </span>
        <span role="cell" className={styles.cell}>
          {release.languages.length > 0 ? release.languages.join(', ') : <span className={ui.muted}>–</span>}
        </span>
      </span>
      <span role="cell" className={styles.actions}>
        {release.infoUrl && (
          <a className={styles.infoLink} href={release.infoUrl} target="_blank" rel="noreferrer" title="Open on the indexer" aria-label="Open on the indexer">
            <svg width="15" height="15" viewBox="0 0 24 24" aria-hidden="true">
              <path d="M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5" fill="none" stroke="currentColor" strokeWidth="1.75" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          </a>
        )}
        {grabbedFile ? (
          <span className={styles.grabbed} title={grabbedFile}>
            Grabbed
          </span>
        ) : (
          <button className={`${ui.button} ${styles.grabButton}`} type="button" disabled={grabbing} onClick={onGrab}>
            {grabbing ? 'Grabbing…' : 'Grab'}
          </button>
        )}
      </span>
    </div>
  )
}
