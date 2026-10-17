import { Link } from 'react-router'
import { downloadPriorities, type DownloadItem } from '../api/downloads'
import ui from '../components/ui.module.css'
import { formatBytes } from '../lib/format'
import { formatEta, formatSpeed, joinParts, percent, relativeTime } from '../status/format'
import StatusIcon from '../status/icons'
import { categoryLabels, statusLabels } from './labels'
import PipelineStrip from './PipelineStrip'
import styles from './Activity.module.css'


export interface DownloadActions {
  pause: (id: number) => void
  resume: (id: number) => void
  retry: (id: number) => void
  priority: (id: number, priority: number) => void
  remove: (item: DownloadItem) => void
}

/** One download in the queue: title, media link, pipeline, progress, numbers and actions. */
export default function DownloadCard({
  item,
  media,
  actions,
  busy,
}: {
  item: DownloadItem
  media: { to: string; label: string } | null
  /** Null for users without configuration access (no buttons). */
  actions: DownloadActions | null
  busy: boolean
}) {
  const post = item.status === 'verifying' || item.status === 'repairing' || item.status === 'extracting'
  const barValue = post ? (item.stepProgress ?? 0) : item.progress
  const priority = downloadPriorities.find((p) => p.value === item.priority)

  return (
    <article className={styles.card} data-status={item.status} aria-label={item.title}>
      <div className={styles.cardHead}>
        <div className={styles.cardTitleBlock}>
          <h3 className={styles.dlTitle}>{item.title}</h3>
          <div className={styles.meta}>
            <span className={`${styles.cat} ${styles[`cat_${item.category}`]}`}>{categoryLabels[item.category]}</span>
            {media && (
              <Link className={styles.mediaLink} to={media.to}>
                {media.label}
              </Link>
            )}
            {item.indexer && <span>{item.indexer}</span>}
            <span>added {relativeTime(item.added)}</span>
          </div>
        </div>

        {actions && (
          <div className={styles.actions}>
            {item.status === 'failed' ? (
              <button type="button" className={styles.iconAction} onClick={() => actions.retry(item.id)} disabled={busy} title="Retry" aria-label={`Retry ${item.title}`}>
                <StatusIcon name="retry" />
              </button>
            ) : item.status === 'paused' ? (
              <button type="button" className={styles.iconAction} onClick={() => actions.resume(item.id)} disabled={busy} title="Resume" aria-label={`Resume ${item.title}`}>
                <StatusIcon name="play" />
              </button>
            ) : (
              <button
                type="button"
                className={styles.iconAction}
                onClick={() => actions.pause(item.id)}
                disabled={busy || post}
                title={post ? 'Post-processing cannot be paused' : 'Pause'}
                aria-label={`Pause ${item.title}`}
              >
                <StatusIcon name="pause" />
              </button>
            )}
            {item.status !== 'failed' && (
            <label className={styles.prioritySelect} title="Priority: higher runs first">
              <span className={styles.srOnly}>Priority of {item.title}</span>
              <select value={item.priority} onChange={(e) => actions.priority(item.id, Number(e.target.value))} disabled={busy || post}>
                {!priority && <option value={item.priority}>Priority {item.priority}</option>}
                {downloadPriorities.map((p) => (
                  <option key={p.value} value={p.value}>
                    {p.label}
                  </option>
                ))}
              </select>
            </label>
            )}
            <button type="button" className={`${styles.iconAction} ${styles.iconDanger}`} onClick={() => actions.remove(item)} disabled={busy} title="Remove" aria-label={`Remove ${item.title}`}>
              <StatusIcon name="trash" />
            </button>
          </div>
        )}
      </div>

      <PipelineStrip item={item} />

      <div className={styles.progressRow}>
        <div
          className={styles.track}
          role="progressbar"
          aria-label={post ? `${statusLabels[item.status]} progress` : 'Download progress'}
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={Math.floor(barValue * 100)}
        >
          <i style={{ width: `${Math.min(100, Math.max(0, barValue * 100))}%` }} />
        </div>
        <span className={styles.pct}>{post ? `${statusLabels[item.status]} ${percent(item.stepProgress)}` : percent(item.progress)}</span>
      </div>

      <div className={styles.numbers}>
        <span>
          <b>{formatBytes(item.downloadedBytes)}</b> of {formatBytes(item.totalBytes)}
        </span>
        {item.status === 'downloading' && (
          <>
            <span>
              <b>{item.speedBps > 0 ? formatSpeed(item.speedBps) : 'waiting for connections'}</b>
            </span>
            {item.speedBps > 0 && item.etaSeconds != null && <span>{formatEta(item.etaSeconds)}</span>}
          </>
        )}
        {item.status === 'queued' && <span>Waiting for a free slot</span>}
        {item.status === 'paused' && <span>Paused</span>}
      </div>

      {(item.missingArticles > 0 || item.statusMessage || item.error) && (
        <div className={styles.notes}>
          {item.missingArticles > 0 && (
            <p className={styles.noteWarn}>
              <StatusIcon name="warn" size={14} />
              {joinParts(
                `${new Intl.NumberFormat().format(item.missingArticles)} missing article${item.missingArticles === 1 ? '' : 's'}`,
                'par2 repair will try to fill the gaps',
              )}
            </p>
          )}
          {item.statusMessage && !item.error && (
            <p className={styles.noteInfo}>
              <StatusIcon name="info" size={14} />
              {item.statusMessage}
            </p>
          )}
          {item.error && (
            <p className={styles.noteError}>
              <StatusIcon name="error" size={14} />
              {item.error}
            </p>
          )}
        </div>
      )}
    </article>
  )
}

export function ConfirmRemove({
  item,
  deleteFiles,
  onDeleteFiles,
}: {
  item: DownloadItem
  deleteFiles: boolean
  onDeleteFiles: (value: boolean) => void
}) {
  return (
    <>
      <p className={styles.dialogText}>
        Remove <span className={ui.mono}>{item.title}</span> {item.status === 'completed' || item.status === 'failed' ? 'from the history' : 'from the queue'}?
      </p>
      <label className={ui.check}>
        <input type="checkbox" checked={deleteFiles} onChange={(e) => onDeleteFiles(e.target.checked)} />
        Also delete the downloaded files
      </label>
    </>
  )
}
