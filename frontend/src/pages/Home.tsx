import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router'
import { api } from '../api/client'
import { libraryApi, queryKeys } from '../api/library'
import { calendarApi, calendarKeys, wantedApi, wantedKeys, type CalendarFilter } from '../api/wanted'
import { addDays, dayKey } from '../calendar/dates'
import { formatBytes } from '../lib/format'
import styles from './Page.module.css'

const pipeline = ['Search', 'Grab', 'Download', 'Verify & Unpack', 'Import & Rename', 'Notify']

export default function Home() {
  const status = useQuery({ queryKey: ['system-status'], queryFn: api.systemStatus })
  const stats = useQuery({ queryKey: queryKeys.stats, queryFn: libraryApi.stats })
  const s = stats.data
  const wanted = useQuery({ queryKey: wantedKeys.summary, queryFn: wantedApi.summary })
  const w = wanted.data
  const [today] = useState(() => new Date())
  const week: CalendarFilter = { start: dayKey(today), end: dayKey(addDays(today, 6)), unmonitored: false, type: 'all' }
  const upcoming = useQuery({ queryKey: calendarKeys.range(week), queryFn: () => calendarApi.entries(week) })
  const next = upcoming.data ?? []
  const nextMovies = next.filter((e) => e.type === 'movie').length

  return (
    <div className={styles.page}>
      <header>
        <h1 className={styles.title}>Home</h1>
        <p className={styles.lead}>One app for your whole media pipeline.</p>
      </header>

      <section className={styles.cards}>
        <Link className={`${styles.card} ${styles.cardLink}`} to="/library/movies" style={{ ['--tone' as string]: 'var(--movies)' }}>
          <div className={styles.label}>Movies</div>
          <div className={styles.value}>{s?.movies ?? '…'}</div>
          <div className={styles.sub}>{s ? `${s.moviesWithFile} on disk` : ''}</div>
        </Link>
        <Link className={`${styles.card} ${styles.cardLink}`} to="/library/series" style={{ ['--tone' as string]: 'var(--series)' }}>
          <div className={styles.label}>Series</div>
          <div className={styles.value}>{s?.series ?? '…'}</div>
          <div className={styles.sub}>{s ? `${s.episodesWithFile} of ${s.episodes} episodes on disk` : ''}</div>
        </Link>
        <div className={styles.card}>
          <div className={styles.label}>Size on disk</div>
          <div className={styles.value}>{s ? formatBytes(s.sizeOnDisk) : '…'}</div>
          <div className={styles.sub}>Movies and episodes</div>
        </div>
        <div className={styles.card}>
          <div className={styles.label}>StreamZH</div>
          <div className={styles.value}>{status.data?.version ?? '…'}</div>
          <div className={styles.sub}>{status.data ? (status.data.isDocker ? 'Docker' : status.data.operatingSystem) : ''}</div>
        </div>
      </section>

      <section className={styles.cards} aria-label="Wanted and upcoming">
        <Link className={`${styles.card} ${styles.cardLink}`} to="/wanted/missing" style={{ ['--tone' as string]: 'var(--accent)' }}>
          <div className={styles.label}>Missing</div>
          <div className={styles.value}>{w ? w.missingMovies + w.missingEpisodes : '…'}</div>
          <div className={styles.sub}>{w ? `${w.missingMovies} movies, ${w.missingEpisodes} episodes` : ''}</div>
        </Link>
        <Link className={`${styles.card} ${styles.cardLink}`} to="/wanted/cutoff" style={{ ['--tone' as string]: 'var(--warn)' }}>
          <div className={styles.label}>Cutoff unmet</div>
          <div className={styles.value}>{w ? w.cutoffUnmetMovies + w.cutoffUnmetEpisodes : '…'}</div>
          <div className={styles.sub}>{w ? `${w.cutoffUnmetMovies} movies, ${w.cutoffUnmetEpisodes} episodes` : ''}</div>
        </Link>
        <Link className={`${styles.card} ${styles.cardLink}`} to="/calendar?view=agenda" style={{ ['--tone' as string]: 'var(--ok)' }}>
          <div className={styles.label}>Next 7 days</div>
          <div className={styles.value}>{upcoming.data ? next.length : '…'}</div>
          <div className={styles.sub}>{upcoming.data ? `${nextMovies} movie releases, ${next.length - nextMovies} episodes` : ''}</div>
        </Link>
      </section>

      {status.isError && <p className={styles.error}>Backend not reachable: {String(status.error)}</p>}

      <section className={styles.panel}>
        <h2 className={styles.panelTitle}>Pipeline</h2>
        <ol className={styles.pipeline}>
          {pipeline.map((stage) => (
            <li key={stage}>{stage}</li>
          ))}
        </ol>
        <p className={styles.muted}>Searching and downloading arrive with versions 0.4 and 0.5. The queue is empty.</p>
      </section>
    </div>
  )
}
