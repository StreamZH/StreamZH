import { useEffect, useId, useRef, useState, type ReactNode } from 'react'
import styles from './MultiSelect.module.css'

export interface Option<T> {
  value: T
  label: string
  hint?: ReactNode
}

/** Filter dropdown with checkboxes. Nothing selected means "Any". Esc closes the list, not the dialog around it. */
export default function MultiSelect<T extends string | number>({
  label,
  options,
  selected,
  onChange,
}: {
  label: string
  options: Option<T>[]
  selected: T[]
  onChange: (values: T[]) => void
}) {
  const [open, setOpen] = useState(false)
  const root = useRef<HTMLDivElement>(null)
  const button = useRef<HTMLButtonElement>(null)
  const listId = useId()

  useEffect(() => {
    if (!open) return
    const onPointer = (e: PointerEvent) => {
      if (root.current && !root.current.contains(e.target as Node)) setOpen(false)
    }
    document.addEventListener('pointerdown', onPointer)
    return () => document.removeEventListener('pointerdown', onPointer)
  }, [open])

  const summary =
    selected.length === 0
      ? 'Any'
      : selected.length === 1
        ? (options.find((o) => o.value === selected[0])?.label ?? String(selected[0]))
        : `${selected.length} selected`

  const toggle = (value: T, checked: boolean) =>
    onChange(checked ? [...selected, value] : selected.filter((v) => v !== value))

  return (
    <div
      ref={root}
      className={styles.root}
      onKeyDown={(e) => {
        if (e.key === 'Escape' && open) {
          e.preventDefault()
          e.stopPropagation()
          setOpen(false)
          button.current?.focus()
        }
      }}
      onBlur={(e) => {
        if (open && root.current && !root.current.contains(e.relatedTarget as Node | null)) setOpen(false)
      }}
    >
      <button
        ref={button}
        type="button"
        className={`${styles.trigger} ${selected.length > 0 ? styles.active : ''}`}
        aria-expanded={open}
        aria-controls={listId}
        onClick={() => setOpen((o) => !o)}
      >
        <span className={styles.label}>{label}</span>
        <span className={styles.value}>{summary}</span>
        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
          <path d="m6 9 6 6 6-6" />
        </svg>
      </button>
      {open && (
        <div id={listId} className={styles.popover} role="group" aria-label={label}>
          {options.map((o) => (
            <label key={String(o.value)} className={styles.option}>
              <input type="checkbox" checked={selected.includes(o.value)} onChange={(e) => toggle(o.value, e.target.checked)} />
              <span className={styles.optionLabel}>{o.label}</span>
              {o.hint && <span className={styles.hint}>{o.hint}</span>}
            </label>
          ))}
          {selected.length > 0 && (
            <button
              type="button"
              className={styles.clear}
              onClick={() => {
                onChange([])
                setOpen(false)
                button.current?.focus()
              }}
            >
              Clear
            </button>
          )}
        </div>
      )}
    </div>
  )
}
