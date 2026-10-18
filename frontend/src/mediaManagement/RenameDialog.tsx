import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router'
import { queryKeys, type MediaType } from '../api/library'
import { mediaManagementApi, mediaManagementKeys } from '../api/mediaManagement'
import Loader from '../components/Loader'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import styles from './MediaManagement.module.css'

/**
 * "Rename files" (Radarr's Organize, Sonarr's Rename): every file whose name differs from the naming scheme, old → new,
 * with a checkbox each for series. Subtitles next to a file are renamed with it.
 */
export default function RenameDialog({ type, id, title, onClose }: { type: MediaType; id: number; title: string; onClose: () => void }) {
  const queryClient = useQueryClient()
  const preview = useQuery({ queryKey: mediaManagementKeys.rename(type, id), queryFn: () => mediaManagementApi.renamePreview(type, id) })
  const [skipped, setSkipped] = useState<Set<string>>(new Set())
  const items = preview.data ?? []
  const chosen = items.filter((i) => !skipped.has(i.existingPath))

  const rename = useMutation({
    mutationFn: () => mediaManagementApi.rename(type, id, undefined, type === 'Series' ? chosen.map((i) => i.existingPath) : undefined),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: mediaManagementKeys.rename(type, id) }),
        queryClient.invalidateQueries({ queryKey: type === 'Movie' ? queryKeys.movie(id) : queryKeys.seriesDetail(id) }),
        queryClient.invalidateQueries({ queryKey: ['history'] }),
      ])
      onClose()
    },
  })

  return (
    <Modal title={`Rename files · ${title}`} onClose={onClose}>
      {preview.isError && <p className={ui.error}>{preview.error.message}</p>}
      {preview.isPending && <Loader text="Checking the file names…" />}
      {preview.isSuccess && items.length === 0 && (
        <p className={styles.dialogText}>
          All files already follow the naming scheme (or renaming is off). The formats are in{' '}
          <Link to="/settings/media-management#movie-naming" onClick={onClose}>
            Settings → Media Management
          </Link>
          .
        </p>
      )}
      {items.length > 0 && (
        <>
          <p className={styles.dialogText}>
            {items.length} file{items.length === 1 ? '' : 's'} can be renamed to the naming scheme. Subtitles with the same name are renamed too.
          </p>
          <ul className={styles.renameList}>
            {items.map((item) => (
              <li key={item.existingPath} className={styles.renameItem}>
                <input
                  type="checkbox"
                  checked={!skipped.has(item.existingPath)}
                  disabled={type === 'Movie'}
                  aria-label={`Rename ${item.existingPath}`}
                  onChange={(e) =>
                    setSkipped((s) => {
                      const next = new Set(s)
                      if (e.target.checked) next.delete(item.existingPath)
                      else next.add(item.existingPath)
                      return next
                    })
                  }
                />
                <span className={styles.renamePaths}>
                  <span className={styles.renameOld}>{item.existingPath}</span>
                  <span className={styles.renameNew}>{item.newPath}</span>
                </span>
              </li>
            ))}
          </ul>
        </>
      )}
      {rename.error && (
        <p className={ui.error} role="alert">
          {rename.error.message}
        </p>
      )}
      <div className={styles.dialogActions}>
        <button type="button" className={ui.button} onClick={onClose}>
          {items.length === 0 ? 'Close' : 'Cancel'}
        </button>
        {items.length > 0 && (
          <button type="button" className={ui.primary} disabled={rename.isPending || chosen.length === 0} onClick={() => rename.mutate()}>
            {rename.isPending ? 'Renaming…' : `Rename ${chosen.length} file${chosen.length === 1 ? '' : 's'}`}
          </button>
        )}
      </div>
    </Modal>
  )
}
