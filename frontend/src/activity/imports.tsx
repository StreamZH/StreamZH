import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router'
import type { DownloadItem } from '../api/downloads'
import { mediaManagementApi, mediaManagementKeys, type ImportState } from '../api/mediaManagement'
import { formatBytes } from '../lib/format'
import { joinParts, relativeTime } from '../status/format'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import { useMediaLinks } from './hooks'
import { useImportActions } from './importHooks'
import { categoryLabels } from './labels'
import ManualImportDialog from './ManualImportDialog'
import PipelineStrip from './PipelineStrip'
import styles from './Activity.module.css'

const stateLabels: Record<ImportState['state'], string> = {
  queued: 'Waiting to import',
  importing: 'Importing…',
  imported: 'Imported',
  failed: 'Import failed',
  waiting: 'Import waits',
}

/**
 * The import line of a completed download: imported files, the reason it failed or waits, and the actions
 * (Retry import, Import…, Dismiss). `admin` false shows the state only.
 */
export function ImportLine({
  item,
  state,
  admin,
  busy,
  onImport,
  onManual,
  onDismiss,
}: {
  item: DownloadItem
  state: ImportState | null
  admin: boolean
  busy: boolean
  onImport: () => void
  onManual: () => void
  onDismiss?: () => void
}) {
  if (item.status !== 'completed') return null
  const linked = item.mediaType != null || state?.manual === true
  const tone = state?.state === 'failed' ? styles.importFailed : state?.state === 'imported' ? styles.importDone : styles.importWaiting

  return (
    <div className={`${styles.importLine} ${tone}`}>
      <div className={styles.importText}>
        <span className={styles.importState}>
          <Icon name={state?.state === 'failed' ? 'alert' : state?.state === 'imported' ? 'check' : state ? 'clock' : 'folder'} size={14} />
          {state ? (
            <>
              {stateLabels[state.state]}
              {state.isUpgrade && state.state === 'imported' && ' (upgrade)'}
              {state.state !== 'queued' && state.state !== 'importing' && <small> · {relativeTime(state.date)}</small>}
            </>
          ) : linked ? (
            'Not imported yet'
          ) : (
            'Not linked to a movie or series'
          )}
        </span>
        {state?.message && <span className={styles.importMessage}>{state.message}</span>}
        {state?.state === 'imported' &&
          state.files.slice(0, 3).map((file) => (
            <span key={file} className={styles.historyPath} title={file}>
              <Icon name="folder" size={13} />
              <span>{file}</span>
            </span>
          ))}
        {state?.state === 'imported' && state.files.length > 3 && <span className={styles.importMore}>and {state.files.length - 3} more files</span>}
      </div>
      {admin && state?.state !== 'queued' && state?.state !== 'importing' && (
        <div className={styles.importActions}>
          {linked && state?.state !== 'imported' && (
            <button type="button" className={`${ui.button} ${ui.sm}`} onClick={onImport} disabled={busy}>
              <Icon name="restart" size={14} /> {state ? 'Retry import' : 'Import now'}
            </button>
          )}
          {state?.state !== 'imported' && (
            <button type="button" className={`${ui.button} ${ui.sm}`} onClick={onManual} disabled={busy}>
              Import…
            </button>
          )}
          {onDismiss && state?.state === 'failed' && (
            <button type="button" className={`${ui.button} ${ui.sm}`} onClick={onDismiss} disabled={busy} title="Hide this from the queue and the status window">
              Dismiss
            </button>
          )}
        </div>
      )}
    </div>
  )
}

/** A completed download that is not imported yet (queued, running, waiting or failed), in the queue. */
export function ImportCard({
  item,
  state,
  media,
  admin,
  busy,
  onImport,
  onManual,
  onDismiss,
}: {
  item: DownloadItem
  state: ImportState
  media: { to: string; label: string } | null
  admin: boolean
  busy: boolean
  onImport: () => void
  onManual: () => void
  onDismiss: () => void
}) {
  return (
    <article className={styles.row} data-status={state.state === 'failed' ? 'failed' : 'importing'} aria-label={item.title}>
      <div className={styles.rowHead}>
        <div className={styles.rowText}>
          <h3 className={styles.rowTitle} title={item.title}>
            {item.title}
          </h3>
          <div className={styles.meta}>
            <span className={`${ui.tag} ${styles.cat} ${styles[`cat_${item.category}`]}`}>{categoryLabels[item.category]}</span>
            {media && (
              <Link className={styles.mediaLink} to={media.to}>
                {media.label}
              </Link>
            )}
            <span>{joinParts(formatBytes(item.totalBytes), item.indexer, `finished ${relativeTime(item.completed ?? item.added)}`)}</span>
          </div>
        </div>
        <span className={`${ui.pill} ${state.state === 'failed' ? ui.pillErr : state.state === 'importing' ? ui.pillBusy : ui.pillWarn} ${styles.statusPill}`}>
          {state.state === 'importing' && <span className={styles.liveDot} aria-hidden="true" />}
          {stateLabels[state.state]}
        </span>
      </div>
      <div className={styles.numbers}>
        <PipelineStrip item={{ ...item, importState: state.state }} />
      </div>
      <ImportLine item={item} state={state} admin={admin} busy={busy} onImport={onImport} onManual={onManual} onDismiss={onDismiss} />
    </article>
  )
}

/** "Waiting for import" in the queue: completed downloads whose import is queued, running, waiting or failed. */
export function PendingImports({
  admin,
  onToast,
  match,
}: {
  admin: boolean
  onToast: (tone: 'ok' | 'error', title: string, detail?: string) => void
  /** Only the downloads this returns true for (the queue filters). */
  match?: (download: DownloadItem) => boolean
}) {
  const pending = useQuery({
    queryKey: mediaManagementKeys.pending,
    queryFn: mediaManagementApi.pending,
    refetchInterval: (q) => (q.state.data?.some((p) => p.import.state === 'queued' || p.import.state === 'importing') ? 2_000 : 15_000),
  })
  const [manual, setManual] = useState<DownloadItem | null>(null)
  const { importNow, dismiss, refresh } = useImportActions(
    (title, message) => onToast('error', title, message),
    (title) => onToast('ok', title),
  )
  const list = (pending.data ?? []).filter((p) => !match || match(p.download))
  const media = useMediaLinks(list.map((p) => p.download))
  if (list.length === 0) return null

  return (
    <section className={styles.pendingSection} aria-labelledby="pending-imports">
      <h2 id="pending-imports" className={styles.sectionTitle}>
        Waiting for import <span className={styles.tcount}>{list.length}</span>
      </h2>
      <div className={styles.list}>
        {list.map(({ download, import: state }) => (
          <ImportCard
            key={download.id}
            item={download}
            state={state}
            media={media(download)}
            admin={admin}
            busy={importNow.isPending || dismiss.isPending}
            onImport={() => importNow.mutate(download.id)}
            onManual={() => setManual(download)}
            onDismiss={() => dismiss.mutate(download.id)}
          />
        ))}
      </div>
      {manual && (
        <ManualImportDialog
          item={manual}
          onClose={() => setManual(null)}
          onQueued={(title) => {
            setManual(null)
            onToast('ok', 'Import queued', title)
            void refresh()
          }}
        />
      )}
    </section>
  )
}
