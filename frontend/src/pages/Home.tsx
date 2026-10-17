import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client'
import styles from './Page.module.css'

const pipeline = ['Search', 'Grab', 'Download', 'Verify & Unpack', 'Import & Rename', 'Notify']

export default function Home() {
  const status = useQuery({ queryKey: ['system-status'], queryFn: api.systemStatus })

  return (
    <div className={styles.page}>
      <header>
        <h1 className={styles.title}>Home</h1>
        <p className={styles.lead}>One app for your whole media pipeline.</p>
      </header>

      <section className={styles.cards}>
        <div className={styles.card}>
          <div className={styles.label}>Version</div>
          <div className={styles.value}>{status.data?.version ?? '...'}</div>
        </div>
        <div className={styles.card}>
          <div className={styles.label}>Database schema</div>
          <div className={styles.value}>{status.data?.databaseSchemaVersion ?? '...'}</div>
        </div>
        <div className={styles.card}>
          <div className={styles.label}>Runtime</div>
          <div className={styles.valueSmall}>{status.data?.runtime ?? '...'}</div>
        </div>
        <div className={styles.card}>
          <div className={styles.label}>Host</div>
          <div className={styles.valueSmall}>
            {status.data ? (status.data.isDocker ? 'Docker' : status.data.operatingSystem) : '...'}
          </div>
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
        <p className={styles.muted}>The queue is empty. Add movies or series to get started.</p>
      </section>
    </div>
  )
}
