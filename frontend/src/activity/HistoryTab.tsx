import { useQuery } from '@tanstack/react-query'
import { useState, type ReactNode } from 'react'
import { Link, useSearchParams } from 'react-router'
import { ApiError } from '../api/client'
import { downloadKeys, downloadsApi, type DownloadHistoryFilter, type DownloadItem } from '../api/downloads'
import { historyApi, historyKeys, type HistoryFilter, type HistoryRecord } from '../api/history'
import Icon, { type IconName } from '../components/Icon'
import Loader from '../components/Loader'
import ui from '../components/ui.module.css'
import Toasts from '../indexerTools/Toast'
import { formatBytes } from '../lib/format'
import { joinParts, relativeTime } from '../status/format'
import { useIsAdmin } from '../status/useOngoing'
import { RemoveDialog } from './Dialogs'
import { downloadTypeOptions, mediaTypeOptions, pick, useCustomCategories } from './filterOptions'
import { FilterBar, FilterSeg, FilterSelect, NoMatches, SearchBox, Toolbar } from './Filters'
import { useDownloadAction, useMediaLinks, useToasts } from './hooks'
import { useImportActions, useImportStates } from './importHooks'
import { ImportLine } from './imports'
import { startOfDay, useClampPage, useListParams } from './listParams'
import ManualImportDialog from './ManualImportDialog'
import { categoryLabels } from './labels'
import Pager from './Pager'
import { usePageSize } from './paging'
import { Empty } from './Empty'
import styles from './Activity.module.css'

/** How each history event shows in the event list. */
const eventLabels: Record<HistoryRecord['eventType'], { label: string; icon: IconName; failed?: boolean; neutral?: boolean }> = {
  Grabbed: { label: 'Grabbed', icon: 'download', neutral: true },
  DownloadCompleted: { label: 'Downloaded', icon: 'check' },
  DownloadFailed: { label: 'Download failed', icon: 'alert', failed: true },
  Imported: { label: 'Imported', icon: 'folder' },
  Upgraded: { label: 'Upgraded', icon: 'arrowup' },
  ImportFailed: { label: 'Import failed', icon: 'alert', failed: true },
  Renamed: { label: 'Renamed', icon: 'rename', neutral: true },
}

const downloadFilterKeys = ['q', 'status', 'import', 'type', 'range'] as const
const eventFilterKeys = ['q', 'event', 'type', 'indexer'] as const

const statusOptions = [
  { value: '', label: 'All' },
  { value: 'completed', label: 'Completed' },
  { value: 'failed', label: 'Failed' },
]

const importOptions = [
  { value: '', label: 'Any import state' },
  { value: 'imported', label: 'Imported' },
  { value: 'notImported', label: 'Not imported' },
  { value: 'importFailed', label: 'Import failed' },
]

const rangeOptions = [
  { value: '', label: 'Any time' },
  { value: 'today', label: 'Today' },
  { value: '7d', label: 'Last 7 days' },
  { value: '30d', label: 'Last 30 days' },
]

/** Days back from the start of today. */
const rangeDays: Record<string, number> = { today: 0, '7d': 6, '30d': 29 }

const eventOptions = [
  { value: '', label: 'All events' },
  ...Object.entries(eventLabels).map(([value, e]) => ({ value, label: e.label })),
]

const eventGroups = [
  {
    label: 'Several',
    options: [
      { value: 'Imported,Upgraded', label: 'Imported or upgraded' },
      { value: 'DownloadFailed,ImportFailed', label: 'All failures' },
    ],
  },
]

/** Finished and failed downloads, and the grab history (what StreamZH sent to the downloader). */
export default function HistoryTab() {
  const [url, setUrl] = useSearchParams()
  const view = url.get('view') === 'grabs' ? 'grabs' : 'downloads'

  // Switching the view starts on page 1 without filters (the two lists filter different things).
  const show = (next: 'downloads' | 'grabs') => setUrl(next === 'grabs' ? { tab: 'history', view: 'grabs' } : { tab: 'history' })

  const views = (
    <div className={ui.seg} role="group" aria-label="History">
      <button type="button" aria-pressed={view === 'downloads'} onClick={() => show('downloads')}>
        Downloads
      </button>
      <button type="button" aria-pressed={view === 'grabs'} onClick={() => show('grabs')}>
        Events
      </button>
    </div>
  )
  return view === 'downloads' ? <DownloadHistory views={views} /> : <GrabHistory views={views} />
}

function DownloadHistory({ views }: { views: ReactNode }) {
  const admin = useIsAdmin()
  const { page, get, setFilters, setSearch, setPage, clear, active } = useListParams(downloadFilterKeys)
  const [pageSize, setPageSize] = usePageSize('activity-downloads')
  const categories = useCustomCategories()
  const status = pick<NonNullable<DownloadHistoryFilter['status']>>(get('status'), statusOptions)
  const importState = status === 'failed' ? '' : pick<NonNullable<DownloadHistoryFilter['import']>>(get('import'), importOptions)
  const range = pick(get('range'), rangeOptions)
  const filter: DownloadHistoryFilter = {
    status: status || undefined,
    import: importState || undefined,
    category: get('type') || undefined,
    search: get('q') || undefined,
    since: range ? startOfDay(rangeDays[range]) : undefined,
  }
  const history = useQuery({
    queryKey: downloadKeys.history(page, pageSize, filter),
    queryFn: () => downloadsApi.history(page, pageSize, filter),
    placeholderData: (previous) => previous,
    retry: (count, error) => !(error instanceof ApiError && error.status === 404) && count < 1,
  })
  const settled = history.data && !history.isPlaceholderData ? history.data.totalRecords : undefined
  useClampPage(page, settled, pageSize, setPage)

  if (history.isError) {
    const missing = history.error instanceof ApiError && history.error.status === 404
    return (
      <>
        <Toolbar>{views}</Toolbar>
        <Empty icon="error" title={missing ? 'No download history yet' : 'The history could not be loaded'}>
          <p>{missing ? 'This StreamZH build has no built-in downloader yet. The grab history shows what was sent.' : history.error.message}</p>
          <div className={styles.emptyActions}>
            <Link className={`${ui.button} ${ui.sm}`} to="/activity?tab=history&view=grabs">
              Grab history
            </Link>
          </div>
        </Empty>
      </>
    )
  }

  if (!history.data) return <Loader text="Loading the history" />

  const total = history.data.totalRecords
  if (total === 0 && !active) {
    return (
      <>
        <Toolbar>{views}</Toolbar>
        <Empty icon="check" title="Nothing finished yet">
          <p>Completed and failed downloads are listed here with their folder or the reason they failed.</p>
        </Empty>
      </>
    )
  }

  return (
    <>
      <FilterBar label="Filter downloads" active={active} matches={settled} noun="downloads" onClear={clear}>
        {views}
        <FilterSeg
          label="Status"
          value={status}
          onChange={(v) => setFilters({ status: v, import: v === 'failed' ? undefined : get('import') })}
          options={statusOptions}
        />
        <FilterSelect label="Import" value={importState} onChange={(v) => setFilters({ import: v })} options={importOptions} disabled={status === 'failed'} />
        <FilterSelect
          label="Type"
          value={get('type')}
          onChange={(v) => setFilters({ type: v })}
          options={downloadTypeOptions}
          groups={[{ label: 'Categories', options: categories }]}
        />
        <FilterSelect label="Finished" value={range} onChange={(v) => setFilters({ range: v })} options={rangeOptions} />
        <SearchBox value={get('q')} onChange={setSearch} placeholder="Search titles" label="Search downloads" />
      </FilterBar>
      {history.data.records.length === 0 ? (
        <NoMatches noun="downloads" onClear={clear} />
      ) : (
        <DownloadRows records={history.data.records} stale={history.isPlaceholderData} admin={admin} />
      )}
      <Pager page={page} pageSize={pageSize} total={total} noun="downloads" onPage={setPage} onPageSize={setPageSize} />
    </>
  )
}

function DownloadRows({ records, stale, admin }: { records: DownloadItem[]; stale: boolean; admin: boolean }) {
  const media = useMediaLinks(records)
  const { toasts, push, dismiss } = useToasts()
  const [removing, setRemoving] = useState<DownloadItem | null>(null)
  const imports = useImportStates(records.filter((r) => r.status === 'completed').map((r) => r.id))
  const importActions = useImportActions(
    (title, message) => push('error', title, message),
    (title) => push('ok', title),
  )
  const [manual, setManual] = useState<DownloadItem | null>(null)
  const retry = useDownloadAction(downloadsApi.retry, (m) => push('error', 'Could not retry', m), () => push('ok', 'Queued again'))
  const remove = useDownloadAction(
    ({ id, deleteFiles }: { id: number; deleteFiles: boolean }) => downloadsApi.remove(id, deleteFiles),
    (m) => push('error', 'Could not remove', m),
    () => setRemoving(null),
  )

  return (
    <>
      <div className={`${ui.panel} ${styles.tablePanel} ${stale ? styles.stale : ''}`}>
        <table className={`${ui.table} ${styles.table}`} aria-label="Download history">
          <thead>
            <tr>
              <th>Status</th>
              <th>Release</th>
              <th className={styles.hideSm}>Type</th>
              <th className={`${styles.r} ${styles.hideSm}`}>Size</th>
              <th className={styles.hideMd}>Indexer</th>
              <th className={styles.hideSm}>Finished</th>
              {admin && (
                <th>
                  <span className={styles.srOnly}>Actions</span>
                </th>
              )}
            </tr>
          </thead>
          <tbody>
            {records.map((item) => {
              const link = media(item)
              const failed = item.status === 'failed'
              const when = relativeTime(item.completed ?? item.added)
              const state = imports(item.id)
              return (
                <tr key={item.id}>
                  <td className={styles.statusCell}>
                    <span className={`${ui.pill} ${failed ? ui.pillErr : ui.pillOk} ${styles.statePill}`} title={failed ? 'Failed' : 'Completed'}>
                      <Icon name={failed ? 'alert' : 'check'} size={13} />
                      <span className={styles.pillText}>{failed ? 'Failed' : 'Completed'}</span>
                    </span>
                  </td>
                  <td className={styles.release}>
                    <span className={styles.relTitle} title={item.title}>
                      {item.title}
                    </span>
                    <span className={styles.sub}>
                      {link && (
                        <Link className={styles.mediaLink} to={link.to}>
                          {link.label}
                        </Link>
                      )}
                      <span className={styles.showSm}>{joinParts(categoryLabels[item.category], formatBytes(item.totalBytes), when)}</span>
                    </span>
                    {failed ? (
                      <span className={styles.historyError}>{item.error ?? 'Failed'}</span>
                    ) : (
                      <>
                        {item.outputPath && state?.state !== 'imported' && (
                          <span className={styles.historyPath} title={item.outputPath}>
                            <Icon name="folder" size={13} />
                            <span>{item.outputPath}</span>
                          </span>
                        )}
                        <ImportLine
                          item={item}
                          state={state}
                          admin={admin}
                          busy={importActions.importNow.isPending}
                          onImport={() => importActions.importNow.mutate(item.id)}
                          onManual={() => setManual(item)}
                        />
                      </>
                    )}
                  </td>
                  <td className={styles.hideSm}>
                    <span className={`${ui.tag} ${styles.cat} ${styles[`cat_${item.category}`]}`}>{categoryLabels[item.category]}</span>
                  </td>
                  <td className={`${styles.r} ${ui.num} ${styles.hideSm} ${styles.nowrap}`}>{formatBytes(item.totalBytes)}</td>
                  <td className={`${styles.hideMd} ${styles.dim}`}>{item.indexer ?? '–'}</td>
                  <td className={`${styles.hideSm} ${styles.dim} ${styles.nowrap}`}>{when}</td>
                  {admin && (
                    <td className={styles.actionsCell}>
                      <div className={styles.actions}>
                        {failed && (
                          <button
                            type="button"
                            className={`${ui.ghost} ${ui.icon} ${ui.sm}`}
                            onClick={() => retry.mutate(item.id)}
                            disabled={retry.isPending}
                            title="Retry"
                            aria-label={`Retry ${item.title}`}
                          >
                            <Icon name="restart" size={16} />
                          </button>
                        )}
                        <button
                          type="button"
                          className={`${ui.ghost} ${ui.icon} ${ui.sm} ${styles.dangerIcon}`}
                          onClick={() => setRemoving(item)}
                          title="Remove"
                          aria-label={`Remove ${item.title}`}
                        >
                          <Icon name="trash" size={16} />
                        </button>
                      </div>
                    </td>
                  )}
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>
      {removing && (
        <RemoveDialog item={removing} busy={remove.isPending} onClose={() => setRemoving(null)} onConfirm={(deleteFiles) => remove.mutate({ id: removing.id, deleteFiles })} />
      )}
      {manual && (
        <ManualImportDialog
          item={manual}
          onClose={() => setManual(null)}
          onQueued={(title) => {
            setManual(null)
            push('ok', 'Import queued', title)
            void importActions.refresh()
          }}
        />
      )}
      <Toasts toasts={toasts} onDismiss={dismiss} />
    </>
  )
}

function GrabHistory({ views }: { views: ReactNode }) {
  const { page, get, setFilters, setSearch, setPage, clear, active } = useListParams(eventFilterKeys)
  const [pageSize, setPageSize] = usePageSize('activity-events')
  const indexers = useQuery({ queryKey: historyKeys.indexers, queryFn: historyApi.indexers, staleTime: 60_000 })
  const event = get('event')
    .split(',')
    .filter((e) => e in eventLabels)
    .join(',')
  const type = pick<NonNullable<HistoryFilter['mediaType']>>(get('type'), mediaTypeOptions)
  const filter: HistoryFilter = {
    page,
    pageSize,
    eventType: event || undefined,
    mediaType: type || undefined,
    search: get('q') || undefined,
    indexer: get('indexer') || undefined,
  }
  const history = useQuery({ queryKey: historyKeys.list(filter), queryFn: () => historyApi.page(filter), placeholderData: (previous) => previous })
  const records = history.data?.records ?? []
  const media = useMediaLinks(records)
  const settled = history.data && !history.isPlaceholderData ? history.data.totalRecords : undefined
  useClampPage(page, settled, pageSize, setPage)

  if (history.isError) {
    return (
      <>
        <Toolbar>{views}</Toolbar>
        <Empty icon="error" title="The grab history could not be loaded">
          <p>{history.error.message}</p>
        </Empty>
      </>
    )
  }
  if (!history.data) return <Loader text="Loading the history" />

  const total = history.data.totalRecords
  if (total === 0 && !active) {
    return (
      <>
        <Toolbar>{views}</Toolbar>
        <Empty icon="download" title="Nothing happened yet">
          <p>Every grab, download, import and rename is recorded here.</p>
        </Empty>
      </>
    )
  }

  const indexerOptions = (indexers.data ?? []).map((name) => ({ value: name, label: name }))
  return (
    <>
      <FilterBar label="Filter events" active={active} matches={settled} noun="events" onClear={clear}>
        {views}
        <FilterSelect label="Event" value={event} onChange={(v) => setFilters({ event: v })} options={eventOptions} groups={eventGroups} />
        <FilterSelect label="Type" value={type} onChange={(v) => setFilters({ type: v })} options={mediaTypeOptions} />
        {(indexerOptions.length > 0 || get('indexer') !== '') && (
          <FilterSelect label="Indexer" value={get('indexer')} onChange={(v) => setFilters({ indexer: v })} options={[{ value: '', label: 'All indexers' }, ...indexerOptions]} />
        )}
        <SearchBox value={get('q')} onChange={setSearch} placeholder="Search release titles" label="Search events" />
      </FilterBar>
      {records.length === 0 ? (
        <NoMatches noun="events" onClear={clear} />
      ) : (
        <div className={`${ui.panel} ${styles.tablePanel} ${history.isPlaceholderData ? styles.stale : ''}`}>
          <table className={`${ui.table} ${styles.table}`} aria-label="Events">
            <thead>
              <tr>
                <th>Event</th>
                <th>Release</th>
                <th className={styles.hideSm}>Quality</th>
                <th className={`${styles.r} ${styles.hideSm}`}>Size</th>
                <th className={styles.hideMd}>Indexer</th>
                <th className={styles.hideSm}>Date</th>
              </tr>
            </thead>
            <tbody>
              {records.map((record: HistoryRecord) => {
                const link = media(record)
                const event = eventLabels[record.eventType] ?? eventLabels.Grabbed
                const detail = record.data.importedPath ?? record.data.to ?? record.data.message ?? record.data.error ?? null
                const when = relativeTime(record.date)
                const quality = record.quality !== 'Unknown' ? record.quality : null
                return (
                  <tr key={record.id}>
                    <td className={styles.statusCell}>
                      <span className={`${ui.pill} ${event.failed ? ui.pillErr : event.neutral ? '' : ui.pillOk} ${styles.statePill}`} title={event.label}>
                        <Icon name={event.icon} size={13} />
                        <span className={styles.pillText}>{event.label}</span>
                      </span>
                    </td>
                    <td className={styles.release}>
                      <span className={styles.relTitle} title={record.sourceTitle}>
                        {record.sourceTitle}
                      </span>
                      <span className={styles.sub}>
                        {link && (
                          <Link className={styles.mediaLink} to={link.to}>
                            {link.label}
                          </Link>
                        )}
                        <span className={styles.showSm}>{joinParts(event.label, quality, record.size > 0 ? formatBytes(record.size) : null, when)}</span>
                      </span>
                      {detail && (
                        <span className={event.failed ? styles.historyError : styles.historyPath} title={detail}>
                          {!event.failed && <Icon name="folder" size={13} />}
                          <span>{detail}</span>
                        </span>
                      )}
                    </td>
                    <td className={styles.hideSm}>{quality ? <span className={ui.tag}>{quality}</span> : <span className={styles.dim}>–</span>}</td>
                    <td className={`${styles.r} ${ui.num} ${styles.hideSm} ${styles.nowrap}`}>{record.size > 0 ? formatBytes(record.size) : '–'}</td>
                    <td className={`${styles.hideMd} ${styles.dim}`}>{record.indexer || '–'}</td>
                    <td className={`${styles.hideSm} ${styles.dim} ${styles.nowrap}`}>{when}</td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
      <Pager page={page} pageSize={pageSize} total={total} noun="events" onPage={setPage} onPageSize={setPageSize} />
    </>
  )
}
