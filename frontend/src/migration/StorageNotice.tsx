import { useMutation, useQueryClient } from '@tanstack/react-query'
import { storageApi, storageKeys, type StorageSettings } from '../api/storage'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import { useIsOwner } from '../settings/useIsOwner'
import styles from './Migration.module.css'

/**
 * 0.12: folders of the old app (or of a path mapping) that are outside the storage locations. StreamZH does not look
 * at them until the owner allows them; the owner does that here in one click, everyone else is told whom to ask.
 */
export default function StorageNotice({ folders, mapped, onAllowed }: { folders: string[]; mapped: boolean; onAllowed: () => void }) {
  const owner = useIsOwner()
  const queryClient = useQueryClient()
  const allow = useMutation({
    mutationFn: async () => {
      let settings: StorageSettings | undefined
      const failed: { folder: string; message: string }[] = []
      for (const folder of folders) {
        try {
          settings = await storageApi.add(folder)
        } catch (error) {
          failed.push({ folder, message: error instanceof Error ? error.message : String(error) })
        }
      }
      if (settings) {
        queryClient.setQueryData(storageKeys.storage, settings)
        onAllowed()
      }
      // Folders that are not there (an old app in Docker): the message says so; a path mapping is the way then.
      if (failed.length > 0) throw new Error(failed.map((f) => `${f.folder}: ${f.message}`).join(' '))
    },
  })

  if (folders.length === 0) return null
  return (
    // The outer div takes the wizard's side padding; the box keeps its own.
    <div>
      <div className={`${ui.infoBox} ${styles.storageNotice}`} role="alert">
      <Icon name="alert" size={15} />
      <div className={styles.storageBody}>
        <p>
          {folders.length === 1 ? 'This folder is' : 'These folders are'} outside the storage locations, so StreamZH does not use{' '}
          {folders.length === 1 ? 'it' : 'them'} yet:
        </p>
        <ul className={styles.storageList}>
          {folders.map((f) => (
            <li key={f} className={ui.mono}>
              {f}
            </li>
          ))}
        </ul>
        {!mapped && (
          <p className={ui.muted}>
            Names from Docker or another machine? Map them to the folders here below instead.
          </p>
        )}
        {owner ? (
          <>
            <div>
              <button type="button" className={`${ui.button} ${ui.sm}`} onClick={() => allow.mutate()} disabled={allow.isPending}>
                <Icon name="check" size={14} />
                {allow.isPending ? 'Allowing…' : folders.length === 1 ? 'Allow as storage location' : 'Allow as storage locations'}
              </button>
            </div>
            {allow.error && <p className={styles.storageError}>{allow.error.message}</p>}
          </>
        ) : (
          <p className={ui.muted}>Ask the owner to add {folders.length === 1 ? 'it' : 'them'} under Settings → General → Storage, then check again.</p>
        )}
      </div>
    </div>
    </div>
  )
}
