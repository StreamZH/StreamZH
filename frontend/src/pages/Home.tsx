import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { api } from '../api/client'
import { libraryApi, queryKeys } from '../api/library'
import { formatBytes } from '../lib/format'
import styles from './Page.module.css'

const pipeline = ['Search', 'Grab', 'Download', 'Verify & Unpack', 'Import & Rename', 'Notify']

export default function Home() {
  const status = useQuery({ queryKey: ['system-status'], queryFn: api.systemStatus })
  const stats = useQuery({ queryKey: queryKeys.stats, queryFn: libraryApi.stats })
  const s = stats.data

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
