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
import { indexerKeys, indexersApi } from '../api/indexers'
import ui from '../components/ui.module.css'
import { formatDateTime, relativeTime } from '../lib/format'
import page from '../pages/Page.module.css'
import styles from './IndexerTools.module.css'

const pageSize = 50

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
  const totalPages = data ? Math.max(1, Math.ceil(data.totalRecords / data.pageSize)) : 1
  const filtered = indexerId != null || eventType != null

  return (
    <div className={`${page.page} ${styles.wide}`}>
      <header>
        <p className={styles.eyebrow}>Indexers</p>
        <h1 className={page.title}>History</h1>
        <p className={page.lead}>Every request StreamZH sent to an indexer, newest first.</p>
      </header>

      <div className={styles.historyFilters}>
        <label className={ui.field}>
          Indexer
          <select className={ui.select} value={indexerId ?? ''} onChange={(e) => update({ indexer: e.target.value || undefined })}>
            <option value="">All indexers</option>
            {(indexers.data ?? []).map((i) => (
              <option key={i.id} value={i.id}>
                {i.name}
              </option>
            ))}
          </select>
        </label>
        <label className={ui.field}>
          Event
          <select className={ui.select} value={eventType ?? ''} onChange={(e) => update({ type: e.target.value || undefined })}>
            <option value="">All events</option>
            {eventTypes.map((t) => (
              <option key={t.value} value={t.value}>
                {t.label}
              </option>
            ))}
          </select>
        </label>
        {filtered && (
          <button className={`${ui.button} ${styles.clearFilters}`} type="button" onClick={() => setUrl({})}>
            Clear filters
          </button>
        )}
        <button className={`${ui.button} ${styles.refresh}`} type="button" onClick={() => void history.refetch()} disabled={history.isFetching}>
          {history.isFetching ? 'Refreshing…' : 'Refresh'}
        </button>
      </div>

      {history.isError ? (
        <div className={styles.state}>
          <p className={ui.error}>{history.error.message}</p>
          <button className={ui.button} type="button" onClick={() => void history.refetch()}>
            Try again
          </button>
        </div>
      ) : !data ? (
        <div className={styles.state} role="status">
          <span className={styles.spinner} aria-hidden="true" />
          <span>Loading history…</span>
        </div>
      ) : data.records.length === 0 ? (
        <div className={styles.empty}>
          <p className={styles.emptyTitle}>{filtered ? 'Nothing matches these filters' : 'No indexer requests yet'}</p>
          <p className={ui.muted}>
            {filtered
              ? 'Try another indexer or event type.'
              : 'Searches, RSS syncs, grabs and indexer tests show up here as soon as StreamZH talks to an indexer.'}
          </p>
        </div>
      ) : (
        <section className={`${styles.historyTable} ${history.isPlaceholderData ? styles.stale : ''}`} role="table" aria-label="Indexer history">
          <div className={`${styles.historyRow} ${styles.historyHead}`} role="row">
            <span role="columnheader">Date</span>
            <span role="columnheader">Indexer</span>
            <span role="columnheader">Event</span>
            <span role="columnheader">Source</span>
            <span role="columnheader">Query / release</span>
            <span role="columnheader" className={styles.right}>
              Results
            </span>
            <span role="columnheader" className={styles.right}>
              Elapsed
            </span>
            <span role="columnheader" aria-label="Status" />
          </div>
          {data.records.map((record) => (
            <HistoryRow key={record.id} record={record} />
          ))}
        </section>
      )}

      {data && data.totalRecords > data.pageSize && (
        <nav className={styles.pager} aria-label="Pages">
          <button className={ui.button} type="button" disabled={pageNumber <= 1} onClick={() => update({ page: String(pageNumber - 1) })}>
            Previous
          </button>
          <span className={styles.pagerText}>
            Page <strong>{pageNumber}</strong> of {totalPages} · {new Intl.NumberFormat().format(data.totalRecords)} requests
          </span>
          <button className={ui.button} type="button" disabled={pageNumber >= totalPages} onClick={() => update({ page: String(pageNumber + 1) })}>
            Next
          </button>
        </nav>
      )}
    </div>
  )
}

function HistoryRow({ record }: { record: IndexerHistoryRecord }) {
  const text = record.eventType === 'grab' ? (record.title ?? record.query) : record.query
  const status = record.successful ? 'Successful' : `Failed: ${record.error ?? 'unknown error'}`
  return (
    <div className={`${styles.historyRow} ${record.successful ? '' : styles.failedRow}`} role="row">
      <span role="cell" className={styles.dateCell} title={formatDateTime(record.date)}>
        {relativeTime(record.date)}
      </span>
      <span role="cell" className={styles.historyIndexer}>
        {record.indexer}
      </span>
      <span role="cell" className={`${styles.event} ${styles[`event_${record.eventType}`] ?? ''}`}>
        <EventIcon type={record.eventType} />
        {eventLabels[record.eventType] ?? record.eventType}
      </span>
      <span role="cell" className={styles.sourceCell}>
        {sourceLabels[record.source] ?? record.source}
      </span>
      <span role="cell" className={styles.queryCell}>
        {text ? <span className={styles.queryText}>{text}</span> : <span className={ui.muted}>–</span>}
        {!record.successful && record.error && <span className={styles.errorText}>{record.error}</span>}
      </span>
      <span role="cell" className={`${styles.num} ${styles.right}`}>
        <span className={styles.mobileLabel}>Results </span>
        {record.results ?? '–'}
      </span>
      <span role="cell" className={`${styles.num} ${styles.right} ${styles.elapsed}`}>
        {formatElapsed(record.elapsedMs)}
      </span>
      <span role="cell" className={styles.statusCell}>
        <span
          className={`${styles.statusDot} ${record.successful ? styles.statusOk : styles.statusFailed}`}
          title={status}
          aria-label={status}
          role="img"
          tabIndex={record.successful ? undefined : 0}
        >
          {record.successful ? '✓' : '!'}
        </span>
      </span>
    </div>
  )
}
