import { useState } from 'react'
import { tmdbImage } from '../lib/format'
import styles from './Poster.module.css'

export default function Poster({ path, title, size = 'w342' }: { path: string | null; title: string; size?: string }) {
  const [failed, setFailed] = useState(false)
  const url = tmdbImage(path, size)

  if (!url || failed) {
    return (
      <div className={styles.fallback} aria-label={title}>
        <span>{title}</span>
      </div>
    )
  }

  return <img className={styles.poster} src={url} alt={title} loading="lazy" onError={() => setFailed(true)} />
}
