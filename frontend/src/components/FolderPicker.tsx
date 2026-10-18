import { useQuery } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { libraryApi } from '../api/library'
import styles from './FolderPicker.module.css'
import Icon from './Icon'
import { Skeleton } from './Loader'
import Modal from './Modal'
import ui from './ui.module.css'

/**
 * Browses folders on the machine StreamZH runs on (server side), so it also works
 * when StreamZH runs on a server or in Docker and the browser is somewhere else.
 */
export default function FolderPicker({
  initialPath,
  onSelect,
  onClose,
}: {
  initialPath?: string
  onSelect: (path: string) => void
  onClose: () => void
}) {
  const [current, setCurrent] = useState<string | null>(initialPath?.trim() || null)
  const [typed, setTyped] = useState(initialPath ?? '')

  const listing = useQuery({
    queryKey: ['filesystem', current],
    queryFn: () => libraryApi.browse(current),
    retry: false,
  })

  const open = (path: string | null) => {
    setCurrent(path)
    setTyped(path ?? '')
  }

  const submitTyped = (event: FormEvent) => {
    event.preventDefault()
    open(typed.trim() || null)
  }

  const shownPath = listing.data?.path ?? null

  return (
    <Modal title="Choose a folder" onClose={onClose}>
      <form className={styles.pathRow} onSubmit={submitTyped}>
        <button
          className={`${ui.button} ${ui.icon}`}
          type="button"
          onClick={() => open(listing.data?.parent ?? null)}
          disabled={!shownPath}
          title="Up one level"
          aria-label="Up one level"
        >
          <Icon name="arrowup" size={16} />
        </button>
        <input
          className={ui.input}
          value={typed}
          onChange={(e) => setTyped(e.target.value)}
          placeholder="Type a path and press Enter"
          aria-label="Path"
          spellCheck={false}
        />
      </form>

      <div className={styles.listBox}>
        {listing.isPending && (
          <div className={styles.note}>
            <Skeleton rows={4} />
          </div>
        )}
        {listing.isError && (
          <div className={`${styles.note} ${styles.error}`}>
            <Icon name="alert" size={16} />
            <span>{listing.error.message}</span>
            <button className={`${ui.button} ${ui.sm}`} type="button" onClick={() => open(null)}>
              Back to drives
            </button>
          </div>
        )}
        {listing.data?.folders.length === 0 && <div className={styles.note}>No subfolders.</div>}
        {listing.data && listing.data.folders.length > 0 && (
          <ul className={styles.list}>
            {listing.data.folders.map((f) => (
              <li key={f.path}>
                <button className={styles.entry} type="button" onClick={() => open(f.path)}>
                  <Icon name={shownPath ? 'folder' : 'hdd'} size={16} className={styles.icon} />
                  <span className={styles.name}>{f.name}</span>
                  {f.hint && <span className={styles.hint}>{f.hint}</span>}
                  <Icon name="right" size={14} className={styles.go} />
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>

      <footer className={styles.footer}>
        <div className={styles.selected}>
          <small>Selected</small>
          {shownPath ? <code>{shownPath}</code> : <span>Pick a drive or folder</span>}
        </div>
        <div className={styles.buttons}>
          <button className={ui.button} type="button" onClick={onClose}>
            Cancel
          </button>
          <button className={ui.primary} type="button" disabled={!shownPath} onClick={() => shownPath && onSelect(shownPath)}>
            <Icon name="check" size={15} />
            Use this folder
          </button>
        </div>
      </footer>
    </Modal>
  )
}
