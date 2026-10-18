import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { libraryApi, queryKeys, type MediaType } from '../api/library'
import { organizeApi, organizeKeys } from '../api/organize'
import Icon from '../components/Icon'
import Modal from '../components/Modal'
import Loader from '../components/Loader'
import ui from '../components/ui.module.css'
import { formatBytes } from '../lib/format'
import styles from './MassEdit.module.css'
import { RunProgress, RunResult } from './RunProgress'
import { useOrganizeStatus } from './useOrganizeStatus'

/** Mass editor → root folder, like Radarr's: move the folders to the new root folder, or only change the library path. */
export default function RootFolderDialog({ type, ids, onClose }: { type: MediaType; ids: number[]; onClose: () => void }) {
  const queryClient = useQueryClient()
  const roots = useQuery({ queryKey: queryKeys.rootFolders, queryFn: libraryApi.rootFolders })
  const status = useOrganizeStatus()
  const choices = (roots.data ?? []).filter((r) => r.mediaType === type)
  const [rootId, setRootId] = useState<number | null>(null)
  const [moveFiles, setMoveFiles] = useState(true)
  const [startedAfter, setStartedAfter] = useState<string | null | undefined>(undefined)
  const selected = rootId
  const noun = type === 'Movie' ? (ids.length === 1 ? 'movie' : 'movies') : 'series'

  const edit = useMutation({
    mutationFn: () => organizeApi.edit(type, ids, { rootFolderId: selected!, moveFiles }),
    onMutate: () => setStartedAfter(status.data?.last?.finished ?? null),
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: type === 'Movie' ? queryKeys.movies : queryKeys.series })
      await queryClient.invalidateQueries({ queryKey: organizeKeys.status })
      if (result.moving === 0) onClose()
    },
  })

  const run = status.data?.running
  const last = status.data?.last
  if (edit.data && edit.data.moving > 0) {
    const finished = !run && last && last.finished !== startedAfter ? last : null
    return (
      <Modal title={finished ? 'Move finished' : 'Moving…'} onClose={onClose}>
        {run && <RunProgress run={run} />}
        {!run && !finished && <Loader text="Starting…" />}
        {finished && <RunResult result={finished} />}
        <div className={styles.actions}>
          <span />
          <button type="button" className={ui.primary} onClick={onClose}>
            {finished ? 'Done' : 'Close'}
          </button>
        </div>
        {!finished && <p className={styles.hint}>You can close this window; the progress also shows in the status window.</p>}
      </Modal>
    )
  }

  return (
    <Modal title={`Root folder for ${ids.length} ${noun}`} onClose={onClose}>
      <label className={ui.field}>
        Root folder
        <select className={ui.select} value={selected ?? ''} onChange={(e) => setRootId(e.target.value ? Number(e.target.value) : null)}>
          <option value="">Choose a root folder…</option>
          {choices.map((r) => (
            <option key={r.id} value={r.id}>
              {r.path}
              {r.freeSpace != null ? ` · ${formatBytes(r.freeSpace)} free` : ''}
            </option>
          ))}
        </select>
      </label>
      <div className={styles.choices} role="radiogroup" aria-label="Files">
        <label className={styles.choice}>
          <input type="radio" name="move" checked={moveFiles} onChange={() => setMoveFiles(true)} />
          <span>
            Move the folders there
            <small>Each folder keeps its name.</small>
          </span>
        </label>
        <label className={styles.choice}>
          <input type="radio" name="move" checked={!moveFiles} onChange={() => setMoveFiles(false)} />
          <span>
            Only change the path
            <small>I move the files myself.</small>
          </span>
        </label>
      </div>
      <p className={styles.hint}>
        {moveFiles
          ? 'To another drive the files are copied first and the old folder is deleted only after the copy is complete.'
          : 'Nothing moves on disk. Until the files are there, the items show as missing.'}
      </p>
      {edit.error && (
        <p className={ui.error} role="alert">
          {edit.error.message}
        </p>
      )}
      <div className={styles.actions}>
        <button type="button" className={ui.button} onClick={onClose}>
          Cancel
        </button>
        <button type="button" className={ui.primary} disabled={selected == null || edit.isPending || (moveFiles && !!run)} onClick={() => edit.mutate()}>
          <Icon name={moveFiles ? 'folder' : 'edit'} size={15} />
          {moveFiles ? `Move ${ids.length} ${noun}` : 'Change path'}
        </button>
      </div>
    </Modal>
  )
}
