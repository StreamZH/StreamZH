import { useMutation, useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import type { MediaType } from '../api/library'
import { releaseKeys, releasesApi, type Command, type SearchFinished, type SearchTarget } from '../api/releases'
import ui from '../components/ui.module.css'
import styles from './ReleaseSearch.module.css'
import ReleaseSearchModal from './ReleaseSearchModal'

export type FinishedEvent = SearchFinished & { at: number }

/**
 * "Search" queues an automatic search (best release is grabbed in the background) and shows how it went;
 * "Manual search" opens the release table.
 */
export default function SearchButtons(props: {
  command: Command
  mediaType: MediaType
  id: number
  target: SearchTarget
  modalTitle: string
  searchLabel?: string
  small?: boolean
}) {
  const [manual, setManual] = useState(false)
  const [startedAt, setStartedAt] = useState<number | null>(null)
  const run = useMutation({
    mutationFn: () => releasesApi.command(props.command),
    onSuccess: () => setStartedAt(Date.now()),
  })

  // Filled by the live connection when the job reports back (see useLiveConnection).
  const finished = useQuery({
    queryKey: releaseKeys.searchFinished(props.mediaType, props.id),
    queryFn: () => Promise.resolve<FinishedEvent | null>(null),
    enabled: false,
  })
  const done = startedAt && finished.data && finished.data.at >= startedAt ? finished.data : null

  let note: { text: string; ok?: boolean } | null = null
  if (run.error) note = { text: run.error.message }
  else if (done) note = done.grabbed > 0 ? { text: `Grabbed ${done.grabbed} release${done.grabbed === 1 ? '' : 's'}`, ok: true } : { text: 'Search done, nothing suitable found' }
  else if (run.isSuccess) note = { text: run.data.queued > 0 ? 'Searching in the background…' : 'A search is already queued' }

  const size = props.small ? ` ${styles.small}` : ''
  return (
    <span className={styles.searchActions}>
      <button className={`${ui.button}${size}`} type="button" onClick={() => run.mutate()} disabled={run.isPending}>
        {props.searchLabel ?? 'Search'}
      </button>
      <button className={`${ui.button}${size}`} type="button" onClick={() => setManual(true)}>
        Manual search
      </button>
      {note && (
        <span className={`${styles.note} ${note.ok ? styles.noteDone : ''} ${run.error ? ui.error : ''}`} role="status">
          {note.text}
        </span>
      )}
      {manual && <ReleaseSearchModal title={props.modalTitle} target={props.target} onClose={() => setManual(false)} />}
    </span>
  )
}
