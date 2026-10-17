import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client'
import page from '../pages/Page.module.css'
import styles from '../pages/Settings.module.css'
import { SystemPage } from './SystemPage'

/** System → Logs: the latest log entries. */
export default function LogsPage() {
  return (
    <SystemPage title="Logs" lead="The latest entries of the StreamZH log. Settings → General sets how much is written.">
      <LogSection />
    </SystemPage>
  )
}

function LogSection() {
  const logs = useQuery({ queryKey: ['logs'], queryFn: () => api.logs(300), refetchInterval: 10_000 })

  return (
    <section className={page.panel}>
      <div className={styles.panelHead}>
        <h2 className={page.panelTitle}>Log</h2>
        <button className={styles.secondary} type="button" onClick={() => void logs.refetch()}>
          Refresh
        </button>
      </div>
      {logs.isError && <p className={styles.error}>{logs.error.message}</p>}
      <pre className={styles.log}>
        {(logs.data?.lines ?? []).length === 0 && <span className={styles.muted}>No entries yet.</span>}
        {[...(logs.data?.lines ?? [])].reverse().map((line, index) => {
          const level = line.split('|')[1] ?? ''
          return (
            <div key={index} className={styles[`level${level}`]}>
              {line}
            </div>
          )
        })}
      </pre>
    </section>
  )
}
