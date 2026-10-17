import { useQuery } from '@tanstack/react-query'
import { historyApi, historyKeys, type HistoryFilter } from '../api/history'
import ui from '../components/ui.module.css'
import { formatBytes, formatDate } from '../lib/format'
import page from '../pages/Page.module.css'
import styles from './ReleaseSearch.module.css'

/** The latest grabs for one movie or series. */
export default function HistoryPanel({ movieId, seriesId }: { movieId?: number; seriesId?: number }) {
  const filter: HistoryFilter = { movieId, seriesId, pageSize: 10 }
  const history = useQuery({ queryKey: historyKeys.list(filter), queryFn: () => historyApi.page(filter) })

  return (
    <section className={page.panel}>
      <h2 className={page.panelTitle}>History</h2>
      {history.isError && <p className={ui.error}>{history.error.message}</p>}
      {history.data && history.data.records.length === 0 && <p className={ui.muted}>Nothing grabbed yet.</p>}
      {history.data && history.data.records.length > 0 && (
        <ul className={styles.history}>
          {history.data.records.map((h) => (
            <li key={h.id}>
              <span className={styles.historyDate} title={new Date(h.date).toLocaleString()}>
                {formatDate(h.date)}
              </span>
              <span className={styles.historyTitle}>{h.sourceTitle}</span>
              <span className={styles.historyMeta}>
                {h.eventType} · {h.quality} · {formatBytes(h.size)}
                {h.indexer && ` · ${h.indexer}`}
              </span>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
