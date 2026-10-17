import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useSearchParams } from 'react-router'
import { ApiError } from '../api/client'
import { downloadKeys, downloadsApi, type DownloadItem } from '../api/downloads'
import { historyApi, historyKeys, type HistoryRecord } from '../api/history'
import ui from '../components/ui.module.css'
import Toasts from '../indexerTools/Toast'
import { formatBytes } from '../lib/format'
import { joinParts, relativeTime } from '../status/format'
import StatusIcon, { type StatusIconName } from '../status/icons'
import { useIsAdmin } from '../status/useOngoing'
import { RemoveDialog } from './Dialogs'
import { useDownloadAction, useMediaLinks, useToasts } from './hooks'
import { useImportActions, useImportStates } from './importHooks'
import { ImportLine } from './imports'
import ManualImportDialog from './ManualImportDialog'
import { categoryLabels } from './labels'
import { Empty } from './QueueTab'
import styles from './Activity.module.css'

const pageSize = 25

/** How each history event shows in the event list. */
const eventLabels: Record<HistoryRecord['eventType'], { label: string; icon: StatusIconName; failed?: boolean; neutral?: boolean }> = {
  Grabbed: { label: 'Grabbed', icon: 'download', neutral: true },
  DownloadCompleted: { label: 'Downloaded', icon: 'check' },
  DownloadFailed: { label: 'Download failed', icon: 'error', failed: true },
  Imported: { label: 'Imported', icon: 'folder' },
  Upgraded: { label: 'Upgraded', icon: 'up' },
  ImportFailed: { label: 'Import failed', icon: 'error', failed: true },
  Renamed: { label: 'Renamed', icon: 'retry', neutral: true },
}

/** Finished and failed downloads, and the grab history (what StreamZH sent to the downloader). */
export default function HistoryTab() {
  const [url, setUrl] = useSearchParams()
  const view = url.get('view') === 'grabs' ? 'grabs' : 'downloads'
  const pageNumber = Math.max(1, Number(url.get('page')) || 1)

  const update = (changes: Record<string, string | undefined>) => {
    const next = new URLSearchParams(url)
    for (const [key, value] of Object.entries(changes)) {
      if (value) next.set(key, value)
      else next.delete(key)
    }
    setUrl(next)
  }

  return (
    <>
      <div className={styles.segmented} role="group" aria-label="History">
        <button type="button" aria-pressed={view === 'downloads'} onClick={() => update({ view: undefined, page: undefined })}>
          Downloads
        </button>
        <button type="button" aria-pressed={view === 'grabs'} onClick={() => update({ view: 'grabs', page: undefined })}>
          Events
        </button>
      </div>
      {view === 'downloads' ? (
        <DownloadHistory page={pageNumber} onPage={(p) => update({ page: p > 1 ? String(p) : undefined })} />
      ) : (
        <GrabHistory page={pageNumber} onPage={(p) => update({ page: p > 1 ? String(p) : undefined })} />
      )}
    </>
  )
}

function DownloadHistory({ page, onPage }: { page: number; onPage: (page: number) => void }) {
  const admin = useIsAdmin()
  const history = useQuery({
    queryKey: downloadKeys.history(page, pageSize),
    queryFn: () => downloadsApi.history(page, pageSize),
    placeholderData: (previous) => previous,
    retry: (count, error) => !(error instanceof ApiError && error.status === 404) && count < 1,
  })
  const records = history.data?.records ?? []
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

  if (history.isError) {
    const missing = history.error instanceof ApiError && history.error.status === 404
    return (
      <Empty icon="error" title={missing ? 'No download history yet' : 'The history could not be loaded'}>
        <p>{missing ? 'This StreamZH build has no built-in downloader yet. The grab history shows what was sent.' : history.error.message}</p>
        <div className={styles.emptyActions}>
          <Link className={ui.button} to="/activity?tab=history&view=grabs">
            Grab history
          </Link>
        </div>
      </Empty>
    )
  }

  if (!history.data) {
    return (
      <div className={styles.loading} role="status">
        <span className={styles.bigSpinner} aria-hidden="true" />
        Loading the history…
      </div>
    )
  }

  if (records.length === 0) {
    return (
      <Empty icon="check" title="Nothing finished yet">
        <p>Completed and failed downloads are listed here with their folder or the reason they failed.</p>
      </Empty>
    )
  }

  return (
    <>
      <ul className={`${styles.historyList} ${history.isPlaceholderData ? styles.stale : ''}`} aria-label="Download history">
        {records.map((item) => {
          const link = media(item)
          const failed = item.status === 'failed'
          return (
            <li key={item.id} className={`${styles.historyItem} ${failed ? styles.historyFailed : ''}`}>
              <span className={styles.historyIcon} title={failed ? 'Failed' : 'Completed'}>
                <StatusIcon name={failed ? 'error' : 'check'} size={16} />
              </span>
              <div className={styles.historyMain}>
                <span className={styles.dlTitle}>{item.title}</span>
                <span className={styles.meta}>
                  <span className={`${styles.cat} ${styles[`cat_${item.category}`]}`}>{categoryLabels[item.category]}</span>
                  {link && (
                    <Link className={styles.mediaLink} to={link.to}>
                      {link.label}
                    </Link>
                  )}
                  <span>{joinParts(formatBytes(item.totalBytes), item.indexer, `${failed ? 'failed' : 'finished'} ${relativeTime(item.completed ?? item.added)}`)}</span>
                </span>
                {failed ? (
                  <span className={styles.historyError}>{item.error ?? 'Failed'}</span>
                ) : (
                  <>
                    {item.outputPath && imports(item.id)?.state !== 'imported' && (
                      <span className={styles.historyPath} title={item.outputPath}>
                        <StatusIcon name="folder" size={13} />
                        {item.outputPath}
                      </span>
                    )}
                    <ImportLine
                      item={item}
                      state={imports(item.id)}
                      admin={admin}
                      busy={importActions.importNow.isPending}
                      onImport={() => importActions.importNow.mutate(item.id)}
                      onManual={() => setManual(item)}
                    />
                  </>
                )}
              </div>
              {admin && (
                <div className={styles.actions}>
                  {failed && (
                    <button type="button" className={styles.iconAction} onClick={() => retry.mutate(item.id)} disabled={retry.isPending} title="Retry" aria-label={`Retry ${item.title}`}>
                      <StatusIcon name="retry" />
                    </button>
                  )}
                  <button type="button" className={`${styles.iconAction} ${styles.iconDanger}`} onClick={() => setRemoving(item)} title="Remove" aria-label={`Remove ${item.title}`}>
                    <StatusIcon name="trash" />
                  </button>
                </div>
              )}
            </li>
          )
        })}
      </ul>
      <Pager page={page} total={history.data.totalRecords} size={history.data.pageSize} noun="downloads" onPage={onPage} />
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

function GrabHistory({ page, onPage }: { page: number; onPage: (page: number) => void }) {
  const filter = { page, pageSize }
  const history = useQuery({ queryKey: historyKeys.list(filter), queryFn: () => historyApi.page(filter), placeholderData: (previous) => previous })
  const records = history.data?.records ?? []
  const media = useMediaLinks(records)

  if (history.isError) {
    return (
      <Empty icon="error" title="The grab history could not be loaded">
        <p>{history.error.message}</p>
      </Empty>
    )
  }
  if (!history.data) {
    return (
      <div className={styles.loading} role="status">
        <span className={styles.bigSpinner} aria-hidden="true" />
        Loading the history…
      </div>
    )
  }
  if (records.length === 0) {
    return (
      <Empty icon="download" title="Nothing happened yet">
        <p>Every grab, download, import and rename is recorded here.</p>
      </Empty>
    )
  }

  return (
    <>
      <ul className={`${styles.historyList} ${history.isPlaceholderData ? styles.stale : ''}`} aria-label="Events">
        {records.map((record: HistoryRecord) => {
          const link = media(record)
          const event = eventLabels[record.eventType] ?? eventLabels.Grabbed
          const detail = record.data.importedPath ?? record.data.to ?? record.data.message ?? record.data.error ?? null
          return (
            <li key={record.id} className={`${styles.historyItem} ${event.failed ? styles.historyFailed : ''}`}>
              <span className={`${styles.historyIcon} ${event.neutral ? styles.historyGrab : ''}`} title={event.label}>
                <StatusIcon name={event.icon} size={16} />
              </span>
              <div className={styles.historyMain}>
                <span className={styles.dlTitle}>{record.sourceTitle}</span>
                <span className={styles.meta}>
                  <span className={styles.eventType}>{event.label}</span>
                  {record.quality !== 'Unknown' && <span className={styles.quality}>{record.quality}</span>}
                  {link && (
                    <Link className={styles.mediaLink} to={link.to}>
                      {link.label}
                    </Link>
                  )}
                  <span>{joinParts(record.size > 0 ? formatBytes(record.size) : null, record.indexer, relativeTime(record.date))}</span>
                </span>
                {detail && (
                  <span className={event.failed ? styles.historyError : styles.historyPath} title={detail}>
                    {!event.failed && <StatusIcon name="folder" size={13} />}
                    {detail}
                  </span>
                )}
              </div>
            </li>
          )
        })}
      </ul>
      <Pager page={page} total={history.data.totalRecords} size={history.data.pageSize} noun="events" onPage={onPage} />
    </>
  )
}

function Pager({ page, total, size, noun, onPage }: { page: number; total: number; size: number; noun: string; onPage: (page: number) => void }) {
  if (total <= size) return null
  const pages = Math.max(1, Math.ceil(total / size))
  return (
    <nav className={styles.pager} aria-label="Pages">
      <button className={ui.button} type="button" disabled={page <= 1} onClick={() => onPage(page - 1)}>
        Previous
      </button>
      <span className={styles.pagerText}>
        Page <strong>{page}</strong> of {pages} · {new Intl.NumberFormat().format(total)} {noun}
      </span>
      <button className={ui.button} type="button" disabled={page >= pages} onClick={() => onPage(page + 1)}>
        Next
      </button>
    </nav>
  )
}
