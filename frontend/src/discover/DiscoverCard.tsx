import { useState } from 'react'
import { badgeFor, posterSrc, type DiscoverItem } from '../api/discover'
import posterStyles from '../components/Poster.module.css'
import styles from './Discover.module.css'
import { useOpenTitle } from './openTitle'

/** A poster from a ready URL (Poster takes a TMDB path); the same look and fallback as Poster. */
export function PosterImg({ src, title }: { src: string | null; title: string }) {
  const [failed, setFailed] = useState(false)
  if (!src || failed) {
    return (
      <span className={posterStyles.fallback} aria-hidden="true">
        <span>{title}</span>
      </span>
    )
  }
  return <img className={posterStyles.poster} src={src} alt="" loading="lazy" onError={() => setFailed(true)} />
}

export default function DiscoverCard({ item, showType = true }: { item: DiscoverItem; showType?: boolean }) {
  const [, setOpen] = useOpenTitle()
  const badge = badgeFor(item.status)
  const kind = item.mediaType === 'movie' ? 'Movie' : 'Series'
  const facts = [item.year ? String(item.year) : null, showType ? kind : null].filter(Boolean).join(' · ')
  return (
    <button
      type="button"
      className={styles.card}
      onClick={() => setOpen(item)}
      aria-label={`${item.title}${item.year ? ` (${item.year})` : ''}, ${kind}${badge ? `, ${badge.label}` : ''}`}
    >
      <span className={styles.poster}>
        <PosterImg src={posterSrc(item)} title={item.title} />
        {badge && <span className={`${styles.flag} ${styles[`flag_${badge.tone}`]}`}>{badge.label}</span>}
      </span>
      <span className={styles.cardText}>
        <span className={styles.cardTitle}>{item.title}</span>
        {facts && <span className={styles.cardMeta}>{facts}</span>}
      </span>
    </button>
  )
}

/** Poster-shaped placeholders while a row or grid loads. */
export function GhostCards({ count = 8 }: { count?: number }) {
  return (
    <>
      {Array.from({ length: count }, (_, i) => (
        <span key={i} className={styles.ghost} aria-hidden="true">
          <span className={styles.ghostPoster} />
          <span className={`sk ${styles.ghostLine}`} />
        </span>
      ))}
    </>
  )
}
