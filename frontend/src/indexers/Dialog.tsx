import { useEffect, useId, useRef, type ReactNode } from 'react'
import Icon from '../components/Icon'
import styles from './Dialog.module.css'

/**
 * Modal dialog for the indexer screens. Like components/Modal (native <dialog> with showModal, so focus stays
 * inside and Esc closes), plus a wide size, a subtitle line and a footer that stays visible while the body scrolls.
 */
export default function Dialog({
  title,
  subtitle,
  size = 'normal',
  tall = false,
  onClose,
  footer,
  headerAction,
  children,
}: {
  title: ReactNode
  subtitle?: ReactNode
  size?: 'normal' | 'wide'
  /** Fixed height, so the dialog does not jump while a list inside is filtered. */
  tall?: boolean
  onClose: () => void
  footer?: ReactNode
  /** Shown left of the close button, e.g. the "Advanced" switch. */
  headerAction?: ReactNode
  children: ReactNode
}) {
  const ref = useRef<HTMLDialogElement>(null)
  const titleId = useId()

  useEffect(() => {
    const dialog = ref.current
    if (dialog && !dialog.open) dialog.showModal()
    return () => dialog?.close()
  }, [])

  return (
    <dialog
      ref={ref}
      className={`${styles.dialog} ${size === 'wide' ? styles.wide : ''} ${tall ? styles.tall : ''}`}
      aria-labelledby={titleId}
      // Only the close button, Cancel and Esc close it: a click next to the window must not throw away what was typed.
      onCancel={(e) => {
        e.preventDefault()
        onClose()
      }}
    >
      <div className={styles.frame}>
        <header className={styles.header}>
          <div className={styles.heading}>
            <h2 id={titleId}>{title}</h2>
            {subtitle && <div className={styles.subtitle}>{subtitle}</div>}
          </div>
          <div className={styles.headerActions}>
            {headerAction}
            <button type="button" className={styles.close} onClick={onClose} aria-label="Close">
              <Icon name="x" size={18} />
            </button>
          </div>
        </header>
        <div className={styles.body}>{children}</div>
        {footer && <footer className={styles.footer}>{footer}</footer>}
      </div>
    </dialog>
  )
}
