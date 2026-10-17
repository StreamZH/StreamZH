import { useEffect, useRef, type ReactNode } from 'react'
import styles from './Modal.module.css'

export default function Modal({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  const ref = useRef<HTMLDialogElement>(null)

  useEffect(() => {
    const dialog = ref.current
    if (dialog && !dialog.open) dialog.showModal()
    return () => dialog?.close()
  }, [])

  return (
    <dialog
      ref={ref}
      className={styles.dialog}
      // Only the close button, Cancel and Esc close it: a click next to the window must not throw away what was typed.
      onCancel={(e) => {
        e.preventDefault()
        onClose()
      }}
    >
      <div className={styles.body}>
        <header className={styles.header}>
          <h2>{title}</h2>
          <button type="button" className={styles.close} onClick={onClose} aria-label="Close">
            ×
          </button>
        </header>
        {children}
      </div>
    </dialog>
  )
}
