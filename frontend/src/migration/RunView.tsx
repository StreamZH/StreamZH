import { useMutation } from '@tanstack/react-query'
import { useEffect, useState, type ReactNode } from 'react'
import { Link } from 'react-router'
import { isLive, migrationApi, type MigrationMessageLevel, type MigrationRun, type MigrationSectionReport } from '../api/migration'
import Icon from '../components/Icon'
import Loader, { Skeleton } from '../components/Loader'
import ui from '../components/ui.module.css'
import { formatDateTime, formatDuration } from '../lib/format'
import { formatElapsed, formatEta } from '../status/format'
import { plural } from './choices'
import { statusPill, totals, useRun } from './hooks'
import styles from './Migration.module.css'
import { sectionLinks, sourceLabel, utc } from './sources'

/**
 * A run: while it runs, percent, time left, the current step, live counts per part and Cancel; afterwards the report.
 * `planned`: the parts picked in the wizard, shown as "waiting" until they start.
 */
export default function RunView({
  id,
  planned = [],
  actions,
}: {
  id: number
  planned?: { key: string; label: string }[]
  /** Buttons under the finished report (Next: Sonarr, Take over now …). */
  actions?: (run: MigrationRun) => ReactNode
}) {
  const run = useRun(id)
  if (run.isError) {
    return (
      <p className={ui.errBox} role="alert">
        <Icon name="alert" size={15} />
        {run.error.message}
      </p>
    )
  }
  if (!run.data) return <Skeleton rows={4} />
  return isLive(run.data) ? <Progress run={run.data} planned={planned} /> : <Report run={run.data} actions={actions?.(run.data)} />
}

function Progress({ run, planned }: { run: MigrationRun; planned: { key: string; label: string }[] }) {
  const cancel = useMutation({ mutationFn: () => migrationApi.cancel(run.id) })
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const t = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(t)
  }, [])
  const elapsed = Math.max(0, (now - utc(run.started).getTime()) / 1000)
  const pct = Math.min(100, Math.max(0, run.progress * 100))
  // Time left from the pace so far; only once there is enough of it to be honest.
  const eta = run.progress > 0.03 && elapsed > 3 ? (elapsed * (1 - run.progress)) / run.progress : null
  const started = new Set(run.report.sections.map((s) => s.key))
  const waiting = planned.filter((p) => !started.has(p.key))
  const current = run.report.sections.length - 1

  return (
    <div className={styles.progress} aria-live="polite">
      <div className={styles.progressTop}>
        <div>
          <div className={ui.eyebrow}>
            {run.dryRun ? 'Dry run' : 'Taking over'} {sourceLabel(run.source)}
          </div>
          <div className={styles.pct}>
            <span className={ui.num}>{Math.floor(pct)}</span>
            <small> %</small>
          </div>
        </div>
        <div className={styles.progressMeta}>
          <span>{run.status === 'queued' ? 'Waiting for another run to finish…' : eta != null ? formatEta(eta) : 'Working…'}</span>
          <span className={ui.muted}>{formatElapsed(elapsed * 1000)} so far</span>
        </div>
      </div>
      <div className={styles.bar} role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.floor(pct)}>
        <i style={{ width: `${Math.max(1.5, pct)}%` }} />
      </div>
      <div className={styles.stepNow}>
        <span className={ui.dotBusy} />
        {run.currentStep ?? 'Starting…'}
      </div>

      <div className={styles.liveTable} role="table" aria-label="Parts">
        <div className={`${styles.liveRow} ${styles.liveHead}`} role="row">
          <span role="columnheader">Part</span>
          <span role="columnheader">New</span>
          <span role="columnheader">Updated</span>
          <span role="columnheader">Linked</span>
          <span role="columnheader">Skipped</span>
          <span role="columnheader">Failed</span>
        </div>
        {run.report.sections.map((s, i) => (
          <div key={s.key} className={styles.liveRow} role="row">
            <span role="cell" className={styles.livePart}>
              <span className={styles.liveIcon}>
                {i === current && run.status === 'running' ? <span className={ui.dotBusy} /> : <Icon name="check" size={14} className={styles.okIcon} />}
              </span>
              {s.label}
            </span>
            <Num n={s.created} />
            <Num n={s.updated} />
            <Num n={s.linked} />
            <Num n={s.skipped} />
            <Num n={s.failed} bad />
          </div>
        ))}
        {waiting.map((p) => (
          <div key={p.key} className={`${styles.liveRow} ${styles.liveWaiting}`} role="row">
            <span role="cell" className={styles.livePart}>
              <Icon name="clock" size={14} />
              {p.label}
            </span>
            <span role="cell" className={styles.liveWait}>
              waiting
            </span>
          </div>
        ))}
      </div>

      <div className={styles.stepActions}>
        <span className={styles.actionNote}>You can leave this page; the run goes on and shows up under Past runs.</span>
        <span className={styles.grow} />
        <button type="button" className={ui.danger} onClick={() => cancel.mutate()} disabled={cancel.isPending || cancel.isSuccess}>
          <Icon name="stop" size={15} />
          {cancel.isPending || cancel.isSuccess ? 'Cancelling…' : 'Cancel'}
        </button>
      </div>
    </div>
  )
}

function Num({ n, bad = false }: { n: number; bad?: boolean }) {
  return (
    <span role="cell" className={`${ui.num} ${n === 0 ? styles.zero : bad ? styles.bad : ''}`}>
      {n.toLocaleString()}
    </span>
  )
}

type LevelFilter = 'all' | 'warn' | 'error' | 'info'

/** The report of a finished run: totals, then each part with its messages (filterable by level). */
export function Report({ run, actions }: { run: MigrationRun; actions?: ReactNode }) {
  const t = totals(run)
  const [level, setLevel] = useState<LevelFilter>('all')
  const [pill, label] = statusPill[run.status]
  const took = run.finished ? (utc(run.finished).getTime() - utc(run.started).getTime()) / 1000 : null

  return (
    <div className={styles.report}>
      <div className={styles.reportHead}>
        <span className={`${ui.pill} ${pill}`}>
          {run.status === 'done' ? <Icon name="check" size={13} /> : <Icon name="alert" size={13} />}
          {run.dryRun && run.status === 'done' ? 'Dry run done' : label}
        </span>
        <span className={styles.reportMeta}>
          {run.app ? `${run.app.appName} ${run.app.version}` : sourceLabel(run.source)} · {formatDateTime(utc(run.started))}
          {took != null && ` · took ${formatDuration(Math.max(1, took))}`}
        </span>
        {run.dryRun && <span className={ui.tag}>Dry run: nothing was changed</span>}
      </div>

      {run.error && run.status !== 'cancelled' && (
        <p className={ui.errBox} role="alert">
          <Icon name="alert" size={15} />
          {run.error}
        </p>
      )}

      <div className={styles.totals}>
        <Total label={run.dryRun ? 'Would be new' : 'New'} value={t.created} tone="ok" />
        <Total label={run.dryRun ? 'Would update' : 'Updated'} value={t.updated} tone="info" />
        <Total label={run.dryRun ? 'Would link' : 'Linked'} value={t.linked} tone="fg" />
        <Total label="Skipped" value={t.skipped} tone="muted" />
        <Total label="Failed" value={t.failed} tone={t.failed > 0 ? 'err' : 'muted'} />
      </div>

      {run.report.sections.length > 0 && (
        <>
          <div className={styles.reportTools}>
            <h3 className={styles.miniTitle}>{plural(run.report.sections.length, 'part')}</h3>
            <div className={ui.seg} role="group" aria-label="Messages">
              {(
                [
                  ['all', 'All', t.warnings + t.errors + t.infos],
                  ['error', 'Errors', t.errors],
                  ['warn', 'Warnings', t.warnings],
                  ['info', 'Info', t.infos],
                ] as [LevelFilter, string, number][]
              ).map(([key, text, n]) => (
                <button key={key} type="button" aria-pressed={level === key} onClick={() => setLevel(key)}>
                  {text} <span className={styles.segCount}>{n.toLocaleString()}</span>
                </button>
              ))}
            </div>
          </div>
          <ul className={styles.reportSections}>
            {run.report.sections.map((s) => (
              <ReportSection key={s.key} section={s} level={level} dryRun={run.dryRun} />
            ))}
          </ul>
        </>
      )}

      {actions && <div className={styles.stepActions}>{actions}</div>}
    </div>
  )
}

function Total({ label, value, tone }: { label: string; value: number; tone: 'ok' | 'info' | 'fg' | 'muted' | 'err' }) {
  return (
    <div className={`${styles.total} ${styles[`tone_${tone}`]}`}>
      <span className={ui.eyebrow}>{label}</span>
      <b className={ui.num}>{value.toLocaleString()}</b>
    </div>
  )
}

const levelOrder: Record<MigrationMessageLevel, number> = { error: 0, warn: 1, info: 2 }

const levelIcon: Record<MigrationMessageLevel, 'info' | 'alert'> = { info: 'info', warn: 'alert', error: 'alert' }

function ReportSection({ section, level, dryRun }: { section: MigrationSectionReport; level: LevelFilter; dryRun: boolean }) {
  const problems = section.failed > 0 || section.messages.some((m) => m.level === 'error')
  const [open, setOpen] = useState(problems)
  const [limit, setLimit] = useState(100)
  // Errors first, then warnings, then the rest, each in the order they came.
  const messages = section.messages
    .filter((m) => level === 'all' || m.level === level)
    .map((m, i) => [m, i] as const)
    .sort(([a, i], [b, j]) => levelOrder[a.level] - levelOrder[b.level] || i - j)
    .map(([m]) => m)
  const link = sectionLinks[section.key]
  const parts = [
    section.created && `${section.created.toLocaleString()} new`,
    section.updated && `${section.updated.toLocaleString()} updated`,
    section.linked && `${section.linked.toLocaleString()} linked`,
    section.skipped && `${section.skipped.toLocaleString()} skipped`,
  ].filter(Boolean)

  return (
    <li className={styles.reportSection}>
      <div className={styles.reportRow}>
        <button type="button" className={styles.reportToggle} aria-expanded={open} onClick={() => setOpen(!open)}>
          <Icon name="down" size={14} className={open ? styles.chevOpen : styles.chev} />
          <span className={styles.reportLabel}>{section.label}</span>
          <span className={styles.reportCounts}>
            {parts.join(' · ') || 'nothing to do'}
            {section.failed > 0 && <b className={styles.bad}> · {section.failed.toLocaleString()} failed</b>}
          </span>
          {messages.length > 0 && <span className={styles.msgCount}>{messages.length.toLocaleString()}</span>}
        </button>
        {link && !dryRun && (
          <Link to={link.to} className={`${ui.ghost} ${ui.sm}`}>
            {link.label}
            <Icon name="right" size={13} />
          </Link>
        )}
      </div>
      {open && (
        <div className={styles.messages}>
          {messages.length === 0 ? (
            <p className={styles.mapEmpty}>{section.messages.length === 0 ? 'No messages.' : 'No messages of this kind.'}</p>
          ) : (
            <ul>
              {messages.slice(0, limit).map((m, i) => (
                <li key={i} className={styles[`msg_${m.level}`]}>
                  <Icon name={levelIcon[m.level]} size={13} />
                  <span>
                    {m.item && <b>{m.item}: </b>}
                    {m.text}
                  </span>
                </li>
              ))}
            </ul>
          )}
          {messages.length > limit && (
            <button type="button" className={`${ui.ghost} ${ui.sm}`} onClick={() => setLimit(limit + 100)}>
              Show {Math.min(100, messages.length - limit)} more of {messages.length.toLocaleString()}
            </button>
          )}
        </div>
      )}
    </li>
  )
}

/** Small "Loading the run" placeholder for dialogs. */
export function RunLoading() {
  return <Loader size="row" text="Loading the run…" />
}
