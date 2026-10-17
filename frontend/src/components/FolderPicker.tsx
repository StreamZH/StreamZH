import { useQuery } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { libraryApi } from '../api/library'
import styles from './FolderPicker.module.css'
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
        <button className={ui.button} type="button" onClick={() => open(listing.data?.parent ?? null)} disabled={!shownPath} title="Up one level">
          ↑ Up
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

      <ul className={styles.list}>
        {listing.isPending && <li className={styles.note}>Loading…</li>}
        {listing.isError && (
          <li className={styles.note}>
            <span className={ui.error}>{listing.error.message}</span>{' '}
            <button className={styles.linkButton} type="button" onClick={() => open(null)}>
              Back to drives
            </button>
          </li>
        )}
        {listing.data?.folders.length === 0 && <li className={styles.note}>No subfolders.</li>}
        {listing.data?.folders.map((f) => (
          <li key={f.path}>
            <button className={styles.entry} type="button" onClick={() => open(f.path)}>
              <span className={styles.icon} aria-hidden="true">
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.75" strokeLinejoin="round">
                  <path d="M3 6.5A1.5 1.5 0 0 1 4.5 5h4.6l2 2.2h8.4A1.5 1.5 0 0 1 21 8.7v9.8a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 18.5z" />
                </svg>
              </span>
              <span className={styles.name}>{f.name}</span>
              {f.hint && <span className={styles.hint}>{f.hint}</span>}
            </button>
          </li>
        ))}
      </ul>

      <footer className={styles.footer}>
        <span className={styles.selected}>{shownPath ?? 'Pick a drive or folder'}</span>
        <button className={ui.button} type="button" onClick={onClose}>
          Cancel
        </button>
        <button className={ui.primary} type="button" disabled={!shownPath} onClick={() => shownPath && onSelect(shownPath)}>
          Use this folder
        </button>
      </footer>
    </Modal>
  )
}
