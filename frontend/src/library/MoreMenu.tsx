import { useEffect, useId, useRef, useState } from 'react'
import Icon, { type IconName } from '../components/Icon'
import ui from '../components/ui.module.css'
import styles from './Detail.module.css'

export interface MoreItem {
  label: string
  icon: IconName
  onClick: () => void
  danger?: boolean
}

/** The «…» button of a detail page: the rarer actions in a small menu that pops open below it. */
export default function MoreMenu({ items, label = 'More actions' }: { items: MoreItem[]; label?: string }) {
  const [open, setOpen] = useState(false)
  const box = useRef<HTMLDivElement>(null)
  const id = useId()

  useEffect(() => {
    if (!open) return
    const onDown = (e: MouseEvent) => {
      if (!box.current?.contains(e.target as Node)) setOpen(false)
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false)
    }
    document.addEventListener('mousedown', onDown)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onDown)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])

  if (items.length === 0) return null
  return (
    <div className={styles.more} ref={box}>
      <button
        type="button"
        className={`${ui.button} ${ui.icon}`}
        aria-label={label}
        title={label}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={open ? id : undefined}
        onClick={() => setOpen((o) => !o)}
      >
        <Icon name="more" size={17} />
      </button>
      {open && (
        <div className={styles.menu} role="menu" id={id}>
          {items.map((item) => (
            <button
              key={item.label}
              type="button"
              role="menuitem"
              className={item.danger ? styles.menuDanger : undefined}
              onClick={() => {
                setOpen(false)
                item.onClick()
              }}
            >
              <Icon name={item.icon} size={15} />
              {item.label}
            </button>
          ))}
        </div>
      )}
    </div>
  )
}
