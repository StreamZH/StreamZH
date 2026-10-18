import { useMutation, useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import type { MediaType } from '../api/library'
import { releaseKeys, releasesApi, type Command, type SearchFinished, type SearchTarget } from '../api/releases'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import styles from './ReleaseSearch.module.css'
import ReleaseSearchModal from './ReleaseSearchModal'

export type FinishedEvent = SearchFinished & { at: number }

function finishedNote(done: SearchFinished): { text: string; ok?: boolean } {
  if (done.grabbed > 0) return { text: `Grabbed ${done.grabbed} release${done.grabbed === 1 ? '' : 's'}`, ok: true }
  if ((done.skipped ?? 0) > 0 && done.skipReason === 'inQueue') return { text: 'Already in the download queue, see Activity', ok: true }
  if ((done.skipped ?? 0) > 0 && done.skipReason === 'recentlyGrabbed')
    return { text: 'Grabbed in the last 12 hours, not searched again. Use Manual search to grab another release.' }
  return { text: 'Search done, nothing suitable found' }
}

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
  /** The automatic search as the page's main button. */
  primary?: boolean
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
  else if (done) note = finishedNote(done)
  else if (run.isSuccess) note = { text: run.data.queued > 0 ? 'Searching in the background…' : 'A search is already queued' }

  const size = props.small ? ` ${ui.sm}` : ''
  const iconSize = props.small ? 14 : 15
  return (
    <span className={styles.searchActions}>
      <button className={`${props.primary ? ui.primary : ui.button}${size}`} type="button" onClick={() => run.mutate()} disabled={run.isPending}>
        <Icon name="search" size={iconSize} />
        {props.searchLabel ?? 'Search'}
      </button>
      <button className={`${ui.button}${size}`} type="button" onClick={() => setManual(true)}>
        <Icon name="list" size={iconSize} />
        Manual search
      </button>
      {note && (
        <span className={`${styles.note} ${note.ok ? styles.noteDone : ''} ${run.error ? ui.error : ''}`} role="status">
          {note.ok && <Icon name="check" size={13} />}
          {note.text}
        </span>
      )}
      {manual && <ReleaseSearchModal title={props.modalTitle} target={props.target} onClose={() => setManual(false)} />}
    </span>
  )
}
