import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { organizeApi, organizeKeys, type LibraryRunProgress, type LibraryRunResult, type OrganizeStatus } from '../api/organize'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import { formatEta } from '../status/format'
import styles from './MassEdit.module.css'

/** The bar of a queued or running organize, undo or move: "42 %", "5 of 12 · Dune · 2 min left". */
export function RunProgress({ run }: { run: LibraryRunProgress }) {
  const percent = Math.floor(run.progress * 100)
  // Time left from the pace so far, measured from the first progress this window saw.
  const [start, setStart] = useState<{ at: number; progress: number } | null>(null)
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(timer)
  }, [])
  if (run.started && !start) setStart({ at: now, progress: run.progress })
  const done = start ? run.progress - start.progress : 0
  const elapsed = start ? (now - start.at) / 1000 : 0
  const eta = done > 0.02 && elapsed > 3 ? (elapsed / done) * (1 - run.progress) : null

  return (
    <div className={styles.progress} role="status" aria-live="polite">
      <div className={styles.progressHead}>
        <strong>{run.title}</strong>
        {run.started ? <span>{percent} %</span> : <span className={styles.waiting}>Waiting for other jobs…</span>}
      </div>
      <div className={styles.progressTrack}>
        <div className={styles.progressFill} style={{ width: `${run.started ? Math.max(2, percent) : 0}%` }} />
      </div>
      {run.started && (
        <span className={styles.progressSub}>
          {run.done} of {run.total}
          {run.current ? ` · ${run.current}` : ''}
          {eta != null ? ` · ${formatEta(eta)}` : ''}
        </span>
      )}
    </div>
  )
}

const verbs: Record<LibraryRunResult['kind'], string> = { organize: 'renamed', undo: 'restored', move: 'moved' }

/** What a finished run did, with the reasons for every item it left out. */
export function RunResult({ result }: { result: LibraryRunResult }) {
  const parts = [
    `${result.done} ${verbs[result.kind]}`,
    result.unchanged > 0 && `${result.unchanged} already fine`,
    result.skipped > 0 && `${result.skipped} skipped`,
    result.failed > 0 && `${result.failed} failed`,
  ].filter(Boolean)
  const bad = result.failed + result.skipped > 0
  return (
    <div className={styles.result}>
      <p className={bad ? styles.resultWarn : styles.resultOk}>
        <Icon name={bad ? 'alert' : 'check'} size={17} />
        {parts.join(' · ')}
      </p>
      {result.errors.length > 0 && (
        <ul className={styles.errors}>
          {result.errors.map((e, i) => (
            <li key={`${e.id}-${i}`}>
              <strong>{e.title}</strong> {e.message}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

/** "Undo" for the last organize while the server still offers it. */
export function UndoButton({ status, label = 'Undo' }: { status: OrganizeStatus | undefined; label?: string }) {
  const queryClient = useQueryClient()
  const undo = useMutation({
    mutationFn: (runId: number) => organizeApi.undo(runId),
    onSettled: () => queryClient.invalidateQueries({ queryKey: organizeKeys.status }),
  })
  if (!status?.undo || status.running) return null
  const runId = status.undo.runId
  return (
    <>
      <button type="button" className={`${ui.button} ${ui.sm}`} disabled={undo.isPending} onClick={() => undo.mutate(runId)}>
        <Icon name="undo" size={14} />
        {undo.isPending ? 'Undoing…' : label}
      </button>
      {undo.error && <span className={ui.error}>{undo.error.message}</span>}
    </>
  )
}
