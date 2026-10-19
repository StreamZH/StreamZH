import { useState } from 'react'
import { isLive, type MigrationRun } from '../api/migration'
import Icon from '../components/Icon'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import { formatDateTime, relativeTime } from '../lib/format'
import { Section } from '../settings/SettingsKit'
import styles from './Migration.module.css'
import { statusPill, summaryLine } from './hooks'
import RunView from './RunView'
import { sourceMeta, utc } from './sources'

/** Past runs (newest first) with their result; a click opens the report. */
export default function RunHistory({ runs }: { runs: MigrationRun[] }) {
  const [open, setOpen] = useState<MigrationRun | null>(null)
  const sorted = [...runs].sort((a, b) => utc(b.started).getTime() - utc(a.started).getTime())
  if (sorted.length === 0) return null
  return (
    <Section id="runs" title="Past runs" description="Every take-over and dry run with its report.">
      <ul className={styles.history}>
        {sorted.map((run) => {
          const meta = sourceMeta(run.source)
          const [pill, label] = statusPill[run.status]
          return (
            <li key={run.id}>
              <button type="button" className={styles.historyRow} onClick={() => setOpen(run)}>
                <span className={styles.historyIcon} aria-hidden="true">
                  <Icon name={meta?.icon ?? 'mountin'} size={16} />
                </span>
                <span className={styles.historyMain}>
                  <span className={styles.historyTitle}>
                    {meta?.label ?? run.source}
                    {run.dryRun && <span className={ui.tag}>Dry run</span>}
                  </span>
                  <span className={styles.historySub} title={formatDateTime(utc(run.started))}>
                    {relativeTime(utc(run.started))}
                    {run.app ? ` · ${run.app.appName} ${run.app.version}` : ''}
                  </span>
                </span>
                <span className={styles.historySummary}>{isLive(run) ? `${Math.floor(run.progress * 100)} %` : summaryLine(run)}</span>
                <span className={`${ui.pill} ${pill}`}>
                  {isLive(run) && <span className={ui.dotBusy} />}
                  {label}
                </span>
                <Icon name="right" size={15} className={styles.historyChev} />
              </button>
            </li>
          )
        })}
      </ul>
      {open && (
        <Modal
          title={`${sourceMeta(open.source)?.label ?? open.source}${open.dryRun ? ' · dry run' : ''}`}
          onClose={() => setOpen(null)}
        >
          <RunView id={open.id} />
        </Modal>
      )}
    </Section>
  )
}
