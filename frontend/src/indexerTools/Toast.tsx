import { useEffect } from 'react'
import { createPortal } from 'react-dom'
import styles from './IndexerTools.module.css'

export interface ToastMessage {
  id: number
  tone: 'ok' | 'error'
  title: string
  detail?: string
}

/** Short notices in the bottom corner; each one goes away after a few seconds or when closed. */
export default function Toasts({ toasts, onDismiss }: { toasts: ToastMessage[]; onDismiss: (id: number) => void }) {
  return createPortal(
    <div className={styles.toasts} role="status" aria-live="polite">
      {toasts.map((toast) => (
        <Toast key={toast.id} toast={toast} onDismiss={onDismiss} />
      ))}
    </div>,
    document.body,
  )
}

function Toast({ toast, onDismiss }: { toast: ToastMessage; onDismiss: (id: number) => void }) {
  useEffect(() => {
    const timer = window.setTimeout(() => onDismiss(toast.id), toast.tone === 'error' ? 9000 : 6000)
    return () => window.clearTimeout(timer)
  }, [toast, onDismiss])

  return (
    <div className={`${styles.toast} ${toast.tone === 'error' ? styles.toastError : styles.toastOk}`}>
      <span className={styles.toastIcon} aria-hidden="true">
        {toast.tone === 'error' ? '!' : '✓'}
      </span>
      <div className={styles.toastBody}>
        <strong>{toast.title}</strong>
        {toast.detail && <span className={styles.toastDetail}>{toast.detail}</span>}
      </div>
      <button type="button" className={styles.toastClose} aria-label="Dismiss" onClick={() => onDismiss(toast.id)}>
        ×
      </button>
    </div>
  )
}
