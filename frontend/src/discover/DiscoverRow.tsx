import { useQuery } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'
import { discoverApi, discoverKeys, listLabels, type DiscoverList } from '../api/discover'
import type { RequestMediaType } from '../api/requests'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import DiscoverCard, { GhostCards } from './DiscoverCard'
import styles from './Discover.module.css'

/** True once the element came near the viewport (rows below the fold load when scrolled to). */
function useNear<T extends Element>(): [React.RefObject<T | null>, boolean] {
  const ref = useRef<T>(null)
  // Without IntersectionObserver every row loads at once.
  const [near, setNear] = useState(() => typeof IntersectionObserver === 'undefined')
  useEffect(() => {
    const el = ref.current
    if (!el || near) return
    const observer = new IntersectionObserver((entries) => entries.some((e) => e.isIntersecting) && setNear(true), { rootMargin: '400px 0px' })
    observer.observe(el)
    return () => observer.disconnect()
  }, [near])
  return [ref, near]
}

/** One list (Trending movies …) as a horizontal row of posters with arrows and "See all". */
export default function DiscoverRow({ type, list }: { type: RequestMediaType; list: DiscoverList }) {
  const [ref, near] = useNear<HTMLElement>()
  const query = useQuery({ queryKey: discoverKeys.list(type, list), queryFn: () => discoverApi.list(type, list), enabled: near, staleTime: 5 * 60_000 })
  const track = useRef<HTMLDivElement>(null)
  const [edges, setEdges] = useState({ start: true, end: false })
  const noun = type === 'movie' ? 'movies' : 'series'
  const title = `${listLabels[list]} ${noun}`
  const items = query.data?.results ?? []

  const update = () => {
    const el = track.current
    if (!el) return
    setEdges({ start: el.scrollLeft < 4, end: el.scrollLeft + el.clientWidth >= el.scrollWidth - 4 })
  }
  useEffect(update, [items.length])

  const scroll = (dir: 1 | -1) => {
    const el = track.current
    if (el) el.scrollBy({ left: dir * Math.max(300, el.clientWidth * 0.85), behavior: 'smooth' })
  }

  return (
    <section ref={ref} className={styles.row} aria-labelledby={`row-${type}-${list}`}>
      <div className={styles.rowHead}>
        <h2 id={`row-${type}-${list}`} className={styles.rowTitle}>
          {title}
        </h2>
        <Link className={styles.seeAll} to={`/discover/${noun}/${list}`}>
          See all
          <Icon name="right" size={14} />
        </Link>
        <div className={styles.rowArrows}>
          <button type="button" className={`${ui.button} ${ui.sm} ${ui.icon}`} onClick={() => scroll(-1)} disabled={edges.start} aria-label={`Scroll ${title} back`}>
            <Icon name="left" size={15} />
          </button>
          <button type="button" className={`${ui.button} ${ui.sm} ${ui.icon}`} onClick={() => scroll(1)} disabled={edges.end || items.length === 0} aria-label={`Scroll ${title} on`}>
            <Icon name="right" size={15} />
          </button>
        </div>
      </div>
      {query.isError ? (
        <p className={`${ui.error} ${styles.rowError}`}>{query.error.message}</p>
      ) : query.data && items.length === 0 ? (
        <p className={`${ui.muted} ${styles.rowError}`}>Nothing here right now.</p>
      ) : (
        <div className={styles.track} ref={track} onScroll={update}>
          {query.data ? items.map((item) => <DiscoverCard key={`${item.mediaType}-${item.tmdbId}`} item={item} showType={false} />) : <GhostCards />}
        </div>
      )}
    </section>
  )
}
