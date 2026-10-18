import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { queryKeys, type MediaType } from '../api/library'
import { organizeApi, type DeleteResult } from '../api/organize'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import { formatBytes } from '../lib/format'
import styles from './MassEdit.module.css'
import { useOrganizeStatus } from './useOrganizeStatus'

export interface RemoveTarget {
  id: number
  title: string
  folder: string
  size: number
}

/**
 * Removes movies or series from StreamZH (Radarr's delete dialog). Files stay unless "Delete files and folder" is on:
 * then the folders go to the recycle bin of Media Management, or are deleted for good when none is set.
 */
export default function RemoveDialog({
  type,
  items,
  onClose,
  onRemoved,
}: {
  type: MediaType
  items: RemoveTarget[]
  onClose: () => void
  onRemoved: (result: DeleteResult) => void
}) {
  const queryClient = useQueryClient()
  const status = useOrganizeStatus()
  const [deleteFiles, setDeleteFiles] = useState(false)
  const recycleBin = status.data?.recycleBin ?? null
  const size = items.reduce((sum, i) => sum + i.size, 0)
  const single = items.length === 1 ? items[0] : null
  const noun = type === 'Movie' ? (items.length === 1 ? 'movie' : 'movies') : 'series'

  const remove = useMutation({
    mutationFn: () => organizeApi.remove(type, items.map((i) => i.id), deleteFiles),
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: type === 'Movie' ? queryKeys.movies : queryKeys.series })
      await queryClient.invalidateQueries({ queryKey: queryKeys.stats })
      if (result.errors.length === 0) onRemoved(result)
    },
  })

  const what = single ? single.title : `${items.length} ${noun}`
  const summary = deleteFiles
    ? single
      ? `Removes ${single.title} from StreamZH and deletes ${single.folder}${single.size > 0 ? ` (${formatBytes(single.size)})` : ''}.`
      : `Removes ${items.length} ${noun} from StreamZH and deletes their folders${size > 0 ? ` (${formatBytes(size)})` : ''}.`
    : `Removes ${what} from StreamZH. The files on disk stay where they are.`

  return (
    <Modal title={`Remove ${what}`} onClose={onClose}>
      <label className={`${ui.check} ${styles.deleteCheck}`}>
        <input type="checkbox" checked={deleteFiles} onChange={(e) => setDeleteFiles(e.target.checked)} />
        Delete files and folder
      </label>
      <p className={styles.hint}>
        {recycleBin
          ? `Deleted folders go to the recycle bin ${recycleBin} and are cleaned up as set in Media Management.`
          : 'No recycle bin is set in Media Management: deleted files are gone for good.'}
      </p>
      <p className={deleteFiles ? styles.summaryDanger : styles.text}>{summary}</p>
      {!single && deleteFiles && (
        <ul className={styles.removeList}>
          {items.slice(0, 8).map((i) => (
            <li key={i.id} className={ui.mono}>
              {i.folder}
            </li>
          ))}
          {items.length > 8 && <li className={ui.muted}>and {items.length - 8} more</li>}
        </ul>
      )}
      {remove.data && remove.data.errors.length > 0 && (
        <ul className={styles.errors}>
          {remove.data.errors.map((e) => (
            <li key={e.id}>
              <strong>{e.title}</strong> {e.message}
            </li>
          ))}
        </ul>
      )}
      {remove.error && (
        <p className={ui.error} role="alert">
          {remove.error.message}
        </p>
      )}
      <div className={styles.actions}>
        <button type="button" className={ui.button} onClick={onClose}>
          {remove.data ? 'Close' : 'Cancel'}
        </button>
        {!remove.data && (
          <button type="button" className={ui.danger} disabled={remove.isPending || items.length === 0} onClick={() => remove.mutate()}>
            {remove.isPending ? 'Removing…' : deleteFiles ? 'Remove and delete files' : 'Remove'}
          </button>
        )}
      </div>
    </Modal>
  )
}
