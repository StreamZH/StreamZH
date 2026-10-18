import { useQuery } from '@tanstack/react-query'
import { historyApi, historyKeys, type HistoryFilter, type HistoryRecord } from '../api/history'
import Icon from '../components/Icon'
import { Skeleton } from '../components/Loader'
import ui from '../components/ui.module.css'
import { formatBytes, formatDate, formatDateTime } from '../lib/format'
import styles from './Detail.module.css'

const events: Record<HistoryRecord['eventType'], { label: string; tone: string }> = {
  Grabbed: { label: 'Grabbed', tone: ui.pillBusy },
  DownloadCompleted: { label: 'Downloaded', tone: ui.pillOk },
  DownloadFailed: { label: 'Download failed', tone: ui.pillErr },
  Imported: { label: 'Imported', tone: ui.pillOk },
  Upgraded: { label: 'Upgraded', tone: ui.pillOk },
  ImportFailed: { label: 'Import failed', tone: ui.pillErr },
  Renamed: { label: 'Renamed', tone: '' },
}

/** The latest grabs for one movie or series. */
export default function HistoryPanel({ movieId, seriesId }: { movieId?: number; seriesId?: number }) {
  const filter: HistoryFilter = { movieId, seriesId, pageSize: 10 }
  const history = useQuery({ queryKey: historyKeys.list(filter), queryFn: () => historyApi.page(filter) })

  return (
    <section className={ui.panel}>
      <div className={ui.panelHead}>
        <h2 className={styles.panelTitle}>
          <Icon name="history" size={17} />
          History
        </h2>
        {history.data && history.data.records.length > 0 && <span className={styles.panelCount}>latest {history.data.records.length}</span>}
      </div>
      {history.isPending && <Skeleton rows={2} />}
      {history.isError && <p className={ui.error}>{history.error.message}</p>}
      {history.data && history.data.records.length === 0 && <p className={ui.muted}>Nothing grabbed yet.</p>}
      {history.data && history.data.records.length > 0 && (
        <table className={`${ui.table} ${styles.historyTable}`}>
          <thead>
            <tr>
              <th>Date</th>
              <th>Event</th>
              <th>Release</th>
              <th>Quality</th>
              <th className={styles.historyNum}>Size</th>
              <th>Indexer</th>
            </tr>
          </thead>
          <tbody>
            {history.data.records.map((h) => {
              const event = events[h.eventType] ?? { label: h.eventType, tone: '' }
              return (
                <tr key={h.id}>
                  <td className={styles.historyDate} title={formatDateTime(h.date)}>
                    {formatDate(h.date)}
                  </td>
                  <td>
                    <span className={`${ui.pill} ${event.tone} ${styles.eventPill}`}>{event.label}</span>
                  </td>
                  <td className={styles.historyRelease}>{h.sourceTitle}</td>
                  <td>
                    <span className={ui.tag}>{h.quality}</span>
                  </td>
                  <td className={styles.historyNum}>{formatBytes(h.size)}</td>
                  <td className={styles.historyMuted}>{h.indexer ?? '–'}</td>
                </tr>
              )
            })}
          </tbody>
        </table>
      )}
    </section>
  )
}
