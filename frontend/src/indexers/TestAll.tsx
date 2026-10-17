import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef } from 'react'
import { indexerKeys, indexersApi } from '../api/indexers'
import ui from '../components/ui.module.css'
import Dialog from './Dialog'
import styles from './Results.module.css'

/** "Test all": tests every enabled indexer in parallel and lists the outcome per indexer. */
export default function TestAll({ onClose, onOpen }: { onClose: () => void; onOpen: (id: number) => void }) {
  const queryClient = useQueryClient()
  const run = useMutation({
    mutationFn: indexersApi.testAll,
    onSettled: () => queryClient.invalidateQueries({ queryKey: indexerKeys.all }),
  })
  const { mutate } = run
  const started = useRef(false)

  // Runs once when the dialog opens (the ref keeps StrictMode from testing twice).
  useEffect(() => {
    if (started.current) return
    started.current = true
    mutate()
  }, [mutate])

  const results = run.data ?? []
  const failed = results.filter((r) => !r.ok).length

  return (
    <Dialog
      title="Test all indexers"
      onClose={onClose}
      footer={
        <>
          <span className={styles.spacer} />
          <button className={ui.button} type="button" onClick={() => run.mutate()} disabled={run.isPending}>
            Test again
          </button>
          <button className={ui.primary} type="button" onClick={onClose}>
            Close
          </button>
        </>
      }
    >
      <div aria-live="polite" className={styles.wrap}>
        {run.isPending && (
          <p className={styles.progress}>
            <span className={styles.spinner} aria-hidden="true" /> Testing every enabled indexer…
          </p>
        )}
        {run.isError && <p className={ui.error}>{run.error.message}</p>}
        {run.isSuccess && results.length === 0 && <p className={ui.muted}>No enabled indexer to test.</p>}
        {run.isSuccess && results.length > 0 && (
          <>
            <p className={styles.summary}>
              {failed === 0 ? (
                <span className={ui.ok}>All {results.length} indexers work.</span>
              ) : (
                <>
                  <span className={ui.ok}>{results.length - failed} work</span>
                  {', '}
                  <span className={ui.error}>{failed} failed</span>.
                </>
              )}
            </p>
            <ul className={styles.results}>
              {results.map((r) => (
                <li key={r.id} className={`${styles.result} ${r.ok ? styles.ok : styles.failed}`}>
                  <span className={styles.icon} aria-label={r.ok ? 'Works' : 'Failed'}>
                    {r.ok ? (
                      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" aria-hidden="true">
                        <path d="m5 12.5 4.5 4.5L19 7.5" />
                      </svg>
                    ) : (
                      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" aria-hidden="true">
                        <path d="M6 6l12 12M18 6 6 18" />
                      </svg>
                    )}
                  </span>
                  <button type="button" className={styles.name} onClick={() => onOpen(r.id)}>
                    {r.name}
                  </button>
                  <span className={styles.message}>{r.message ?? (r.ok ? 'Connection works.' : 'Failed.')}</span>
                </li>
              ))}
            </ul>
          </>
        )}
      </div>
    </Dialog>
  )
}
