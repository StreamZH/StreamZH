import { useInfiniteQuery, useQuery } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { Link, useParams, useSearchParams } from 'react-router'
import { discoverApi, discoverKeys, listLabels, type DiscoverItem, type DiscoverList, type DiscoverPage as Page } from '../api/discover'
import type { RequestMediaType } from '../api/requests'
import { ApiError } from '../api/client'
import { useIsAdmin } from '../status/useOngoing'
import { useMe, limitText } from '../api/me'
import Icon from '../components/Icon'
import PageHeader from '../components/PageHeader'
import ui from '../components/ui.module.css'
import DetailDrawer from './DetailDrawer'
import DiscoverCard, { GhostCards } from './DiscoverCard'
import DiscoverRow from './DiscoverRow'
import styles from './Discover.module.css'

type Kind = 'all' | RequestMediaType

const lists: DiscoverList[] = ['trending', 'popular', 'upcoming', 'topRated']

const kinds: { value: Kind; label: string; icon?: 'film' | 'tv' }[] = [
  { value: 'all', label: 'All' },
  { value: 'movie', label: 'Movies', icon: 'film' },
  { value: 'series', label: 'Series', icon: 'tv' },
]

/** Next page of a TMDB-style page result, or undefined at the end. */
const nextPage = (last: Page) => (last.page < last.totalPages ? last.page + 1 : undefined)

/** Usage of the request limits: "2 of 5 movies this week · 0 of 2 series this week". */
function LimitLine() {
  const me = useMe()
  const parts = [limitText(me.data?.usage?.movies, 'movies'), limitText(me.data?.usage?.series, 'series')].filter(Boolean)
  if (parts.length === 0) return null
  return (
    <span className={styles.limitLine}>
      <Icon name="gauge" size={14} />
      {parts.join(' · ')}
    </span>
  )
}

/** A grid of results that loads more pages on demand (search results and "See all"). */
function ResultGrid({ items, loading, error, hasMore, loadingMore, onMore, empty }: {
  items: DiscoverItem[]
  loading: boolean
  error: Error | null
  hasMore: boolean
  loadingMore: boolean
  onMore: () => void
  empty: string
}) {
  if (error) return <p className={ui.error}>{error.message}</p>
  if (loading)
    return (
      <div className={styles.grid}>
        <GhostCards count={12} />
      </div>
    )
  if (items.length === 0)
    return (
      <div className={ui.empty}>
        <Icon name="search" size={22} />
        <p>{empty}</p>
      </div>
    )
  return (
    <>
      <div className={styles.grid}>
        {items.map((item) => (
          <DiscoverCard key={`${item.mediaType}-${item.tmdbId}`} item={item} />
        ))}
      </div>
      {hasMore && (
        <div className={styles.more}>
          <button type="button" className={ui.button} onClick={onMore} disabled={loadingMore}>
            {loadingMore ? 'Loading…' : 'Show more'}
          </button>
        </div>
      )}
    </>
  )
}

/** Discover: rows of TMDB lists for movies and series, a search, and the detail drawer to request a title. */
export default function DiscoverPage() {
  const [params, setParams] = useSearchParams()
  const kind = (['movie', 'series'].includes(params.get('type') ?? '') ? params.get('type') : 'all') as Kind
  const urlQuery = params.get('q') ?? ''
  const [text, setText] = useState(urlQuery)
  const [seenQuery, setSeenQuery] = useState(urlQuery)
  const query = urlQuery.trim()

  // The URL changed from outside (command palette, Back): the input follows it.
  if (urlQuery !== seenQuery) {
    setSeenQuery(urlQuery)
    setText(urlQuery)
  }

  // Debounced search: the URL (and with it the query) follows the input after a short pause.
  useEffect(() => {
    if (text === urlQuery) return
    const t = window.setTimeout(() => {
      const next = new URLSearchParams(params)
      if (text.trim()) next.set('q', text)
      else next.delete('q')
      setParams(next, { replace: true })
    }, 350)
    return () => window.clearTimeout(t)
  }, [text, urlQuery, params, setParams])

  const search = useInfiniteQuery({
    queryKey: discoverKeys.search(query),
    queryFn: ({ pageParam }) => discoverApi.search(query, pageParam),
    initialPageParam: 1,
    getNextPageParam: nextPage,
    enabled: query.length > 0,
  })

  const setKind = (value: Kind) => {
    const next = new URLSearchParams(params)
    if (value === 'all') next.delete('type')
    else next.set('type', value)
    setParams(next, { replace: true })
  }

  // Without a TMDB key every list fails the same way: one notice instead of an error in each row (1.0 RC).
  const admin = useIsAdmin()
  const first = useQuery({ queryKey: discoverKeys.list('movie', 'trending'), queryFn: () => discoverApi.list('movie', 'trending'), staleTime: 5 * 60_000 })
  const tmdbMissing = first.error instanceof ApiError && first.error.code === 'tmdb-not-configured'

  const results = (search.data?.pages.flatMap((p) => p.results) ?? []).filter((r) => kind === 'all' || r.mediaType === kind)
  const types: RequestMediaType[] = kind === 'all' ? ['movie', 'series'] : [kind]

  return (
    <div className={styles.page}>
      <PageHeader title="Discover" lead="Find movies and series and request what you want to watch." actions={<LimitLine />} />

      <div className={styles.toolbar}>
        <div className={ui.seg} role="group" aria-label="Show">
          {kinds.map((k) => (
            <button key={k.value} type="button" aria-pressed={kind === k.value} onClick={() => setKind(k.value)}>
              {k.icon && <Icon name={k.icon} size={15} />}
              {k.label}
            </button>
          ))}
        </div>
        <label className={styles.search}>
          <Icon name="search" size={15} />
          <input
            className={ui.input}
            type="search"
            placeholder="Search movies and series"
            aria-label="Search movies and series"
            value={text}
            onChange={(e) => setText(e.target.value)}
            onKeyDown={(e) => e.key === 'Escape' && setText('')}
          />
        </label>
      </div>

      {tmdbMissing ? (
        <div className={`${ui.panel} ${ui.empty}`}>
          <Icon name="compass" size={22} />
          <h3>Discover needs TMDB</h3>
          <p>
            {admin
              ? 'Add your free TMDB key, and Discover shows what is trending, popular and coming soon.'
              : 'An admin has to add a TMDB key first. Then Discover shows what is trending, popular and coming soon.'}
          </p>
          {admin && (
            <Link className={ui.primary} to="/get-started">
              Add a TMDB key
              <Icon name="right" size={14} />
            </Link>
          )}
        </div>
      ) : query ? (
        <section aria-label="Search results" className={styles.results}>
          <p className={styles.resultHead}>
            Results for <b>“{query}”</b>
            {search.isFetching && !search.isFetchingNextPage && <span className={ui.dotBusy} aria-label="Searching" />}
          </p>
          <ResultGrid
            items={results}
            loading={search.isPending}
            error={search.error}
            hasMore={!!search.hasNextPage}
            loadingMore={search.isFetchingNextPage}
            onMore={() => void search.fetchNextPage()}
            empty={`Nothing found for “${query}”.`}
          />
        </section>
      ) : (
        <div className={styles.rows}>
          {lists.flatMap((list) => types.map((type) => <DiscoverRow key={`${type}-${list}`} type={type} list={list} />))}
        </div>
      )}

      <DetailDrawer />
    </div>
  )
}

/** "See all": one list as a paged grid (/discover/movies/trending). */
export function DiscoverListPage() {
  const { type: typeParam, list: listParam } = useParams()
  const type: RequestMediaType = typeParam === 'series' ? 'series' : 'movie'
  const list = (Object.keys(listLabels).includes(listParam ?? '') ? listParam : 'trending') as DiscoverList
  const noun = type === 'movie' ? 'movies' : 'series'
  const pages = useInfiniteQuery({
    queryKey: [...discoverKeys.list(type, list), 'all'],
    queryFn: ({ pageParam }) => discoverApi.list(type, list, pageParam),
    initialPageParam: 1,
    getNextPageParam: nextPage,
  })
  const items = pages.data?.pages.flatMap((p) => p.results) ?? []

  return (
    <div className={styles.page}>
      <PageHeader
        eyebrow={
          <Link to="/discover" className={styles.back}>
            <Icon name="left" size={13} />
            Discover
          </Link>
        }
        title={`${listLabels[list]} ${noun}`}
        actions={<LimitLine />}
      />
      <ResultGrid
        items={items}
        loading={pages.isPending}
        error={pages.error}
        hasMore={!!pages.hasNextPage}
        loadingMore={pages.isFetchingNextPage}
        onMore={() => void pages.fetchNextPage()}
        empty="Nothing here right now."
      />
      <DetailDrawer />
    </div>
  )
}
