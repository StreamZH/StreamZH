import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import { Link } from 'react-router'
import type { OngoingNotice, OngoingRunning } from '../api/status'
import { formatElapsed, formatSpeed, percent } from './format'
import StatusIcon from './icons'
import { loadDismissed, loadOpen, saveDismissed, saveOpen } from './storage'
import { useOngoing } from './useOngoing'
import styles from './StatusWindow.module.css'

/** A download row that is waiting (queued or paused) rather than working. */
const isIdle = (row: OngoingRunning) => row.kind === 'download' && row.speedBps == null

/**
 * The status window at the bottom right, as in CloudZH: collapsed one row with the essentials (spinner, title, percent and
 * speed or elapsed time, "+N", a thin progress bar), expanded the running work and a "Heads-up" list of notices.
 * Administrators only; hidden while nothing runs and nothing needs attention.
 */
export default function StatusWindow() {
  const { admin, data } = useOngoing()
  const [open, setOpen] = useState(loadOpen)
  const [dismissed, setDismissed] = useState(loadDismissed)
  const headRef = useRef<HTMLButtonElement>(null)
  const collapseRef = useRef<HTMLButtonElement>(null)
  const [now, setNow] = useState(() => Date.now())

  const running = useMemo(() => data?.running ?? [], [data])
  const notices = useMemo(
    () => (data?.notices ?? []).filter((n) => !(n.dismissible && dismissed.has(n.key))),
    [data, dismissed],
  )
  const visible = admin && running.length + notices.length > 0


  const active = running.filter((r) => !isIdle(r))
  const lead = active[0] ?? running[0]
  const ticking = visible && !open && lead != null && lead.progress == null && !isIdle(lead)
  useEffect(() => {
    if (!ticking) return
    const tick = () => setNow(Date.now())
    const timer = window.setInterval(tick, 1000)
    const first = window.setTimeout(tick, 0)
    return () => {
      window.clearInterval(timer)
      window.clearTimeout(first)
    }
  }, [ticking])

  useEffect(() => {
    document.body.classList.toggle('sz-has-dock', visible)
    return () => document.body.classList.remove('sz-has-dock')
  }, [visible])

  if (!visible) return null

  const toggle = (next: boolean) => {
    setOpen(next)
    saveOpen(next)
    // Focus stays in the window: on "Collapse" when expanded, on the header when collapsed.
    window.requestAnimationFrame(() => (next ? collapseRef : headRef).current?.focus({ preventScroll: true }))
  }

  const dismiss = (key: string) => {
    const next = new Set(dismissed)
    next.add(key)
    setDismissed(next)
    saveDismissed(next)
  }

  const onKeyDown = (e: KeyboardEvent) => {
    if (e.key === 'Escape' && open) {
      e.stopPropagation()
      toggle(false)
    }
  }

  // ---- Collapsed header: the most important row ----
  let icon: ReactNode
  let title: string
  let meta = ''
  let progress: number | null = null
  let tone: 'run' | 'idle' | 'info' | 'warn' | 'error'
  if (lead) {
    title = lead.title
    progress = lead.progress
    if (isIdle(lead)) {
      tone = 'idle'
      icon = <StatusIcon name="pause" size={14} />
      meta = lead.sub?.split(' · ')[0] ?? ''
    } else {
      tone = 'run'
      icon = <span className={styles.spinner} />
      meta =
        lead.speedBps != null && lead.progress != null
          ? `${percent(lead.progress)} · ${formatSpeed(lead.speedBps)}`
          : lead.progress != null
            ? percent(lead.progress)
            : formatElapsed(now - (lead.seenAt ?? now))
    }
  } else {
    const first = notices[0]
    title = first.title
    tone = first.level
    icon = <StatusIcon name={first.level} />
  }
  const more = running.length + notices.length - 1
  const waiting = running.length - active.length
  const count = [
    active.length ? `${active.length} running` : '',
    waiting ? `${waiting} waiting` : '',
    notices.length ? (notices.length === 1 ? '1 notice' : `${notices.length} notices`) : '',
  ]
    .filter(Boolean)
    .join(' · ')
  const collapseSoon = () => window.setTimeout(() => toggle(false), 0)

  return (
    <div className={`${styles.dock} ${open ? styles.open : ''}`} role="region" aria-label="Status" onKeyDown={onKeyDown}>
      <div className={styles.panel} id="sz-status-panel" hidden={!open}>
        <div className={styles.ph}>
          <b>Status</b>
          <span className={styles.count}>{count}</span>
          <button ref={collapseRef} type="button" className={styles.iconButton} onClick={() => toggle(false)} title="Collapse" aria-label="Collapse">
            <StatusIcon name="down" size={16} />
          </button>
        </div>
        <div className={styles.body}>
          {running.length > 0 && (
            <div className={styles.list}>
              {running.map((row) => (
                <Row key={row.key} title={row.title} sub={row.sub} link={row.link} tone={isIdle(row) ? 'idle' : 'run'} progress={row.progress} onNavigate={collapseSoon} />
              ))}
            </div>
          )}
          {notices.length > 0 && (
            <section className={styles.list} aria-label="Heads-up">
              <div className={styles.sec}>Heads-up</div>
              {notices.map((notice) => (
                <NoticeRow key={notice.key} notice={notice} onNavigate={collapseSoon} onDismiss={() => dismiss(notice.key)} />
              ))}
            </section>
          )}
        </div>
      </div>

      <button
        ref={headRef}
        type="button"
        className={`${styles.head} ${styles[tone]}`}
        hidden={open}
        aria-expanded={open}
        aria-controls="sz-status-panel"
        title="Show all running work and notices"
        onClick={() => toggle(true)}
      >
        <span className={styles.ic}>{icon}</span>
        <span className={styles.tx}>
          <span className={styles.t}>{title}</span>
          {meta && <span className={styles.m}>{meta}</span>}
        </span>
        {more > 0 && (
          <span className={styles.n} aria-label={`${more} more`}>
            +{more}
          </span>
        )}
        <span className={styles.chev}>
          <StatusIcon name="up" size={16} />
        </span>
        {progress != null && <i className={styles.bar} style={{ width: `${Math.round(progress * 1000) / 10}%` }} />}
      </button>
    </div>
  )
}

function NoticeRow({ notice, onNavigate, onDismiss }: { notice: OngoingNotice; onNavigate: () => void; onDismiss: () => void }) {
  return (
    <Row title={notice.title} sub={notice.sub} link={notice.link} tone={notice.level} onNavigate={onNavigate}>
      {notice.dismissible && (
        <button type="button" className={styles.cx} onClick={onDismiss} title="Hide" aria-label={`Hide: ${notice.title}`}>
          <StatusIcon name="x" size={14} />
        </button>
      )}
    </Row>
  )
}

function Row({
  title,
  sub,
  link,
  tone,
  progress,
  onNavigate,
  children,
}: {
  title: string
  sub: string | null
  link: string | null
  tone: 'run' | 'idle' | 'info' | 'warn' | 'error'
  progress?: number | null
  onNavigate: () => void
  children?: ReactNode
}) {
  const content = (
    <>
      <span className={styles.st}>
        {tone === 'run' ? <span className={styles.spinner} /> : tone === 'idle' ? <StatusIcon name="pause" size={14} /> : <StatusIcon name={tone} />}
      </span>
      <span className={styles.ctx}>
        <span className={styles.ct} title={title}>
          {title}
        </span>
        {sub && <span className={styles.cs}>{sub}</span>}
      </span>
    </>
  )
  return (
    <div className={`${styles.chip} ${styles[tone]}`}>
      {link ? (
        <Link className={styles.cb} to={link} onClick={onNavigate}>
          {content}
        </Link>
      ) : (
        <div className={`${styles.cb} ${styles.static}`}>{content}</div>
      )}
      {children}
      {(tone === 'run' || tone === 'idle') && progress != null && (
        <i className={styles.cbar} style={{ width: `calc((100% - 20px) * ${Math.min(1, Math.max(0, progress))})` }} />
      )}
    </div>
  )
}
