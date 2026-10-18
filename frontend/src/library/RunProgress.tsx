import { useMutation, useQueryClient } from '@tanstack/react-query'
import { organizeApi, organizeKeys, type LibraryRunProgress, type LibraryRunResult, type OrganizeStatus } from '../api/organize'
import ui from '../components/ui.module.css'
import styles from './MassEdit.module.css'

/** The bar of a queued or running organize, undo or move: "5 of 12 · Dune". */
export function RunProgress({ run }: { run: LibraryRunProgress }) {
  const percent = Math.floor(run.progress * 100)
  return (
    <div className={styles.progress} role="status" aria-live="polite">
      <div className={styles.progressHead}>
        <strong>{run.title}</strong>
        <span>{run.started ? `${percent} %` : 'Waiting for other jobs…'}</span>
      </div>
      <div className={styles.progressTrack}>
        <div className={styles.progressFill} style={{ width: `${run.started ? Math.max(2, percent) : 0}%` }} />
      </div>
      {run.started && (
        <span className={styles.progressSub}>
          {run.done} of {run.total}
          {run.current ? ` · ${run.current}` : ''}
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
      <p className={bad ? styles.resultWarn : styles.resultOk}>{parts.join(' · ')}</p>
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
      <button type="button" className={ui.button} disabled={undo.isPending} onClick={() => undo.mutate(runId)}>
        {undo.isPending ? 'Undoing…' : label}
      </button>
      {undo.error && <span className={ui.error}>{undo.error.message}</span>}
    </>
  )
}
