import { Link } from 'react-router'
import { downloadPriorities, type DownloadItem } from '../api/downloads'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import { formatBytes } from '../lib/format'
import { formatSpeed, joinParts, relativeTime } from '../status/format'
import { formatLeft, precisePercent } from './format'
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

/** Status pill tone per download status (CloudZH .pill). */
const pillTone: Record<DownloadItem['status'], string> = {
  queued: '',
  downloading: ui.pillBusy,
  paused: styles.pillMuted,
  verifying: styles.pillInfo,
  repairing: styles.pillInfo,
  extracting: styles.pillInfo,
  completed: ui.pillOk,
  failed: ui.pillErr,
}

/** "12.4 / 29.1 GB" with one unit when both sizes share it. */
function sizePair(done: number, total: number): string {
  const a = formatBytes(done)
  const b = formatBytes(total)
  const unit = b.split(' ')[1]
  return unit && a.endsWith(` ${unit}`) ? `${a.slice(0, -unit.length - 1)} / ${b}` : `${a} / ${b}`
}

/** One download in the queue: title, status, a slim progress bar with exact percent and time left, and the actions. */
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
  const left = item.status === 'downloading' && item.speedBps > 0 ? formatLeft(item.etaSeconds) : ''

  return (
    <article className={styles.row} data-status={item.status} aria-label={item.title}>
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
            <span>{joinParts(item.indexer, `added ${relativeTime(item.added)}`)}</span>
          </div>
        </div>

        <span className={`${ui.pill} ${pillTone[item.status]} ${styles.statusPill}`}>
          {item.status === 'downloading' && <span className={styles.liveDot} aria-hidden="true" />}
          {statusLabels[item.status]}
          {post && <span className={ui.num}>{precisePercent(item.stepProgress)}</span>}
        </span>

        {actions && (
          <div className={styles.actions}>
            {item.status !== 'failed' && (
              <label className={styles.priority} title="Priority: higher runs first">
                <span className={styles.srOnly}>Priority of {item.title}</span>
                <select
                  className={`${ui.select} ${styles.prioritySelect}`}
                  value={item.priority}
                  onChange={(e) => actions.priority(item.id, Number(e.target.value))}
                  disabled={busy || post}
                >
                  {!priority && <option value={item.priority}>Priority {item.priority}</option>}
                  {downloadPriorities.map((p) => (
                    <option key={p.value} value={p.value}>
                      {p.label}
                    </option>
                  ))}
                </select>
              </label>
            )}
            {item.status === 'failed' ? (
              <button type="button" className={`${ui.ghost} ${ui.icon} ${ui.sm}`} onClick={() => actions.retry(item.id)} disabled={busy} title="Retry" aria-label={`Retry ${item.title}`}>
                <Icon name="restart" size={16} />
              </button>
            ) : item.status === 'paused' ? (
              <button type="button" className={`${ui.ghost} ${ui.icon} ${ui.sm}`} onClick={() => actions.resume(item.id)} disabled={busy} title="Resume" aria-label={`Resume ${item.title}`}>
                <Icon name="play" size={16} />
              </button>
            ) : (
              <button
                type="button"
                className={`${ui.ghost} ${ui.icon} ${ui.sm}`}
                onClick={() => actions.pause(item.id)}
                disabled={busy || post}
                title={post ? 'Post-processing cannot be paused' : 'Pause'}
                aria-label={`Pause ${item.title}`}
              >
                <Icon name="pause" size={16} />
              </button>
            )}
            <button
              type="button"
              className={`${ui.ghost} ${ui.icon} ${ui.sm} ${styles.dangerIcon}`}
              onClick={() => actions.remove(item)}
              disabled={busy}
              title="Remove"
              aria-label={`Remove ${item.title}`}
            >
              <Icon name="trash" size={16} />
            </button>
          </div>
        )}
      </div>

      <div className={styles.progress}>
        <div
          className={styles.bar}
          role="progressbar"
          aria-label={post ? `${statusLabels[item.status]} progress` : 'Download progress'}
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={Math.floor(barValue * 1000) / 10}
          aria-valuetext={precisePercent(barValue)}
        >
          <i style={{ width: `${Math.min(100, Math.max(0, barValue * 100))}%` }} />
        </div>
        <span className={styles.pct}>{precisePercent(barValue)}</span>
      </div>

      <div className={styles.numbers}>
        <span className={styles.size}>{post ? formatBytes(item.totalBytes) : sizePair(item.downloadedBytes, item.totalBytes)}</span>
        {item.status === 'downloading' &&
          (item.speedBps > 0 ? (
            <>
              <span>
                <Icon name="download" size={13} />
                {formatSpeed(item.speedBps)}
              </span>
              {left && (
                <span className={styles.left}>
                  <Icon name="clock" size={13} />
                  {left} left
                </span>
              )}
            </>
          ) : (
            <span>Waiting for connections</span>
          ))}
        {item.status === 'queued' && <span>Waiting for a free slot</span>}
        <PipelineStrip item={item} />
      </div>

      {(item.missingArticles > 0 || item.statusMessage || item.error) && (
        <div className={styles.notes}>
          {item.missingArticles > 0 && (
            <p className={styles.noteWarn}>
              <Icon name="alert" size={14} />
              {joinParts(
                `${new Intl.NumberFormat().format(item.missingArticles)} missing article${item.missingArticles === 1 ? '' : 's'}`,
                'par2 repair will try to fill the gaps',
              )}
            </p>
          )}
          {item.statusMessage && !item.error && (
            <p className={styles.noteInfo}>
              <Icon name="info" size={14} />
              {item.statusMessage}
            </p>
          )}
          {item.error && (
            <p className={styles.noteError}>
              <Icon name="alert" size={14} />
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
