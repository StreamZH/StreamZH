import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { renamerApi, renamerKeys, type RenamerRun, type RunState } from '../../api/renamer'
import ui from '../../components/ui.module.css'
import { formatDateTime } from '../../lib/format'
import styles from './Renamer.module.css'
import { activeStates } from './rules'

const stateLabel: Record<RunState, string> = {
  queued: 'Queued',
  running: 'Renaming…',
  completed: 'Done',
  partial: 'Partly done',
  failed: 'Failed',
  undoing: 'Undoing…',
  undone: 'Undone',
}

/** Every run with what it did; Undo puts the names back (in reverse order) and removes folders it created. */
export default function HistoryPanel({ runs }: { runs: RenamerRun[] }) {
  const queryClient = useQueryClient()
  const [openId, setOpenId] = useState<number | null>(null)
  const [confirm, setConfirm] = useState<number | null>(null)
  const undo = useMutation({
    mutationFn: (id: number) => renamerApi.undo(id),
    onSettled: () => {
      setConfirm(null)
      void queryClient.invalidateQueries({ queryKey: renamerKeys.all })
    },
  })

  if (runs.length === 0) return <p className={styles.emptyRows}>No renames yet. Every run is listed here and can be undone.</p>

  return (
    <ul className={styles.history}>
      {runs.map((run) => {
        const busy = activeStates.includes(run.state)
        const canUndo = (run.state === 'completed' || run.state === 'partial' || run.state === 'failed') && run.done > 0
        return (
          <li key={run.id} className={styles.historyItem}>
            <div className={styles.historyHead}>
              <div className={styles.historyText}>
                <span className={styles.historyTitle}>
                  {run.state === 'undone' ? `${run.total} renamed, then put back` : `${run.done} of ${run.total} renamed`}
                  <span className={`${styles.runState} ${styles[`run_${run.state}`] ?? ''}`}>{stateLabel[run.state]}</span>
                </span>
                <span className={styles.historyMeta}>
                  {formatDateTime(run.started)} · <span className={ui.mono}>{run.folder}</span>
                </span>
                <span className={styles.historyMeta}>{run.summary}</span>
                {run.error && <span className={ui.error}>{run.error}</span>}
              </div>
              <div className={styles.historyActions}>
                <button type="button" className={ui.button} onClick={() => setOpenId(openId === run.id ? null : run.id)} aria-expanded={openId === run.id}>
                  {openId === run.id ? 'Hide' : 'Details'}
                </button>
                {canUndo &&
                  (confirm === run.id ? (
                    <>
                      <button type="button" className={ui.danger} disabled={undo.isPending} onClick={() => undo.mutate(run.id)}>
                        {undo.isPending ? 'Undoing…' : `Undo ${run.done}`}
                      </button>
                      <button type="button" className={ui.button} onClick={() => setConfirm(null)}>
                        Keep
                      </button>
                    </>
                  ) : (
                    <button type="button" className={ui.button} onClick={() => setConfirm(run.id)} disabled={busy}>
                      Undo
                    </button>
                  ))}
              </div>
            </div>
            {undo.error && undo.variables === run.id && <p className={ui.error}>{undo.error.message}</p>}
            {openId === run.id && <RunItems id={run.id} folder={run.folder} live={busy} />}
          </li>
        )
      })}
    </ul>
  )
}

function RunItems({ id, folder, live }: { id: number; folder: string; live: boolean }) {
  const detail = useQuery({ queryKey: renamerKeys.run(id), queryFn: () => renamerApi.run(id), refetchInterval: live ? 1000 : false })
  if (detail.isPending) return <p className={styles.emptyRows}>Loading…</p>
  if (detail.isError) return <p className={ui.error}>{detail.error.message}</p>
  const relative = (path: string) => (path.startsWith(folder) ? path.slice(folder.length).replace(/^[/\\]/, '') : path)
  return (
    <ul className={styles.runItems}>
      {detail.data.items.map((item) => (
        <li key={item.order} className={`${styles.runItem} ${item.state === 'failed' ? styles.runItemFailed : ''}`}>
          {item.kind === 'createFolder' ? (
            <span>
              Created folder <code>{relative(item.newPath)}</code>
            </span>
          ) : item.kind === 'removeFolder' ? (
            <span>
              Removed empty folder <code>{relative(item.oldPath)}</code>
            </span>
          ) : (
            <span className={styles.runPaths}>
              <code>{relative(item.oldPath)}</code>
              <span aria-hidden="true">→</span>
              <code>{relative(item.newPath)}</code>
            </span>
          )}
          <span className={styles.runItemState}>{item.error ?? item.state}</span>
        </li>
      ))}
    </ul>
  )
}
