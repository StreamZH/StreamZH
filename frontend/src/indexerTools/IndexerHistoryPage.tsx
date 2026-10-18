import { useQuery } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { useSearchParams } from 'react-router'
import {
  indexerActivityApi,
  indexerActivityKeys,
  type IndexerEventSource,
  type IndexerEventType,
  type IndexerHistoryRecord,
} from '../api/indexerActivity'
import Pager from '../activity/Pager'
import { usePageSize } from '../activity/paging'
import { indexerKeys, indexersApi } from '../api/indexers'
import Icon from '../components/Icon'
import Loader from '../components/Loader'
import PageHeader from '../components/PageHeader'
import ui from '../components/ui.module.css'
import { formatDateTime, relativeTime } from '../lib/format'
import styles from './IndexerTools.module.css'

const eventTypes: { value: IndexerEventType; label: string }[] = [
  { value: 'query', label: 'Query' },
  { value: 'rss', label: 'RSS' },
  { value: 'grab', label: 'Grab' },
  { value: 'caps', label: 'Capabilities' },
]

const eventLabels: Record<IndexerEventType, string> = { query: 'Query', rss: 'RSS', grab: 'Grab', caps: 'Caps' }

const sourceLabels: Record<IndexerEventSource, string> = {
  interactive: 'Manual search',
  automatic: 'Automatic search',
  rss: 'RSS sync',
  search: 'Search page',
  test: 'Test',
}

const eventIcons: Record<IndexerEventType, ReactNode> = {
  query: (
    <>
      <circle cx="11" cy="11" r="6" />
      <path d="m19 19-3.5-3.5" />
    </>
  ),
  rss: (
    <>
      <path d="M5 11a8 8 0 0 1 8 8M5 5a14 14 0 0 1 14 14" />
      <circle cx="6" cy="18" r="1.2" fill="currentColor" />
    </>
  ),
  grab: <path d="M12 4v11m0 0-4.5-4.5M12 15l4.5-4.5M5 20h14" />,
  caps: <path d="M4 7h10M18 7h2M4 17h4M12 17h8M16 5v4M10 15v4" />,
}

function EventIcon({ type }: { type: IndexerEventType }) {
  return (
    <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      {eventIcons[type] ?? eventIcons.query}
    </svg>
  )
}

function formatElapsed(ms: number): string {
  return ms >= 10_000 ? `${(ms / 1000).toFixed(1)} s` : `${Math.round(ms)} ms`
}


/** Every API call StreamZH made to an indexer: searches, RSS, grabs and capability reads (like Prowlarr's History). */
export default function IndexerHistoryPage() {
  const [url, setUrl] = useSearchParams()
  const indexerId = Number(url.get('indexer')) || undefined
  const typeParam = url.get('type')
  const eventType = eventTypes.some((t) => t.value === typeParam) ? (typeParam as IndexerEventType) : undefined
  const pageNumber = Math.max(1, Number(url.get('page')) || 1)
  const [pageSize, setPageSize] = usePageSize('indexer-history')

  const indexers = useQuery({ queryKey: indexerKeys.list, queryFn: indexersApi.list })
  const filter = { page: pageNumber, pageSize, indexerId, eventType }
  const history = useQuery({
    queryKey: indexerActivityKeys.history(filter),
    queryFn: () => indexerActivityApi.history(filter),
    placeholderData: (previous) => previous,
  })

  const update = (changes: Record<string, string | undefined>) => {
    const next = new URLSearchParams(url)
    for (const [key, value] of Object.entries(changes)) {
      if (value) next.set(key, value)
      else next.delete(key)
    }
    if (!('page' in changes)) next.delete('page')
    setUrl(next)
  }

  const data = history.data
  const filtered = indexerId != null || eventType != null
  const total = data?.totalRecords ?? 0

  return (
    <div className={styles.page}>
      <PageHeader
        title="History"
        lead={
          data && total > 0 ? (
            <>
              <b className={styles.leadNum}>{new Intl.NumberFormat().format(total)}</b> requests to your indexers, newest first.
            </>
          ) : (
            'Every request StreamZH sent to an indexer, newest first.'
          )
        }
        actions={
          <button className={ui.button} type="button" onClick={() => void history.refetch()} disabled={history.isFetching}>
            <Icon name="restart" size={15} className={history.isFetching ? styles.spinIcon : undefined} />
            {history.isFetching ? 'Refreshing…' : 'Refresh'}
          </button>
        }
      />

      <div className={styles.filterBar} role="search" aria-label="Filter the history">
        <select
          className={`${ui.select} ${styles.toolSelect} ${indexerId != null ? styles.toolActive : ''}`}
          aria-label="Indexer"
          title="Indexer"
          value={indexerId ?? ''}
          onChange={(e) => update({ indexer: e.target.value || undefined })}
        >
          <option value="">All indexers</option>
          {(indexers.data ?? []).map((i) => (
            <option key={i.id} value={i.id}>
              {i.name}
            </option>
          ))}
        </select>
        <div className={ui.seg} role="group" aria-label="Event">
          <button type="button" aria-pressed={eventType == null} onClick={() => update({ type: undefined })}>
            All
          </button>
          {eventTypes.map((t) => (
            <button key={t.value} type="button" aria-pressed={eventType === t.value} onClick={() => update({ type: t.value })}>
              {t.label}
            </button>
          ))}
        </div>
        {filtered && (
          <button className={styles.clearLink} type="button" onClick={() => setUrl({})}>
            <Icon name="x" size={13} /> Clear filters
          </button>
        )}
      </div>

      {history.isError ? (
        <div className={ui.panel}>
          <div className={ui.empty}>
            <h3>The history could not be loaded</h3>
            <p className={ui.error}>{history.error.message}</p>
            <button className={`${ui.button} ${ui.sm}`} type="button" onClick={() => void history.refetch()}>
              Try again
            </button>
          </div>
        </div>
      ) : !data ? (
        <Loader text="Loading the history" />
      ) : data.records.length === 0 ? (
        <div className={ui.panel}>
          <div className={ui.empty}>
            <span className={styles.emptyIcon}>
              <Icon name="history" size={20} />
            </span>
            <h3>{filtered ? 'Nothing matches these filters' : 'No indexer requests yet'}</h3>
            <p>
              {filtered
                ? 'Try another indexer or event type.'
                : 'Searches, RSS syncs, grabs and indexer tests show up here as soon as StreamZH talks to an indexer.'}
            </p>
          </div>
        </div>
      ) : (
        <div className={`${ui.panel} ${styles.tablePanel} ${history.isPlaceholderData ? styles.stale : ''}`}>
          <table className={`${ui.table} ${styles.table}`} aria-label="Indexer history">
            <thead>
              <tr>
                <th aria-label="Status" />
                <th>Date</th>
                <th className={styles.hideSm}>Indexer</th>
                <th className={styles.hideSm}>Event</th>
                <th className={styles.hideMd}>Source</th>
                <th>Query / release</th>
                <th className={styles.right}>Results</th>
                <th className={`${styles.right} ${styles.hideSm}`}>Elapsed</th>
              </tr>
            </thead>
            <tbody>
              {data.records.map((record) => (
                <HistoryRow key={record.id} record={record} />
              ))}
            </tbody>
          </table>
        </div>
      )}

      {data && (
        <Pager
          page={pageNumber}
          pageSize={pageSize}
          total={total}
          noun="requests"
          onPage={(p) => update({ page: p > 1 ? String(p) : undefined })}
          onPageSize={setPageSize}
        />
      )}
    </div>
  )
}

function HistoryRow({ record }: { record: IndexerHistoryRecord }) {
  const text = record.eventType === 'grab' ? (record.title ?? record.query) : record.query
  const status = record.successful ? 'Successful' : `Failed: ${record.error ?? 'unknown error'}`
  return (
    <tr className={record.successful ? undefined : styles.failedRow}>
      <td className={styles.statusCell}>
        <span
          className={record.successful ? ui.dotOk : ui.dotErr}
          title={status}
          aria-label={status}
          role="img"
          tabIndex={record.successful ? undefined : 0}
        />
      </td>
      <td className={styles.dateCell} title={formatDateTime(record.date)}>
        {relativeTime(record.date)}
      </td>
      <td className={`${styles.historyIndexer} ${styles.hideSm}`}>{record.indexer}</td>
      <td className={styles.hideSm}>
        <span className={`${styles.event} ${styles[`event_${record.eventType}`] ?? ''}`}>
          <EventIcon type={record.eventType} />
          {eventLabels[record.eventType] ?? record.eventType}
        </span>
      </td>
      <td className={`${styles.sourceCell} ${styles.hideMd}`}>{sourceLabels[record.source] ?? record.source}</td>
      <td className={styles.queryCell}>
        <span className={styles.showSm}>
          <b>{record.indexer}</b>
          <span className={styles.event}>
            <EventIcon type={record.eventType} />
            {eventLabels[record.eventType] ?? record.eventType}
          </span>
        </span>
        {text ?<span className={styles.queryText}>{text}</span> : <span className={ui.muted}>–</span>}
        {!record.successful && record.error && <span className={styles.errorText}>{record.error}</span>}
      </td>
      <td className={`${styles.num} ${styles.right}`}>{record.results ?? '–'}</td>
      <td className={`${styles.num} ${styles.right} ${styles.elapsed} ${styles.hideSm}`}>{formatElapsed(record.elapsedMs)}</td>
    </tr>
  )
}
