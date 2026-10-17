import { useEffect, useId, useRef, useState } from 'react'
import styles from './IndexerTools.module.css'

export interface MultiOption {
  value: number
  label: string
  /** Sub-option of this value: shown indented, and covered (checked, disabled) while the parent is chosen. */
  parent?: number
  hint?: string
}

/**
 * A compact dropdown with checkboxes. Nothing chosen means "all" (the placeholder says so).
 * Choosing a parent drops its sub-options, since the parent already includes them.
 */
export default function MultiSelect({
  label,
  placeholder,
  options,
  selected,
  onChange,
}: {
  label: string
  placeholder: string
  options: MultiOption[]
  selected: number[]
  onChange: (values: number[]) => void
}) {
  const [open, setOpen] = useState(false)
  const root = useRef<HTMLDivElement>(null)
  const id = useId()

  useEffect(() => {
    if (!open) return
    const onPointer = (event: PointerEvent) => {
      if (root.current && !root.current.contains(event.target as Node)) setOpen(false)
    }
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false)
    }
    document.addEventListener('pointerdown', onPointer)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('pointerdown', onPointer)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])

  const chosen = new Set(selected)
  const names = options.filter((o) => chosen.has(o.value)).map((o) => o.label)
  const summary = names.length === 0 ? placeholder : names.length <= 2 ? names.join(', ') : `${names.length} selected`

  const toggle = (option: MultiOption) => {
    const next = new Set(chosen)
    if (next.has(option.value)) {
      next.delete(option.value)
    } else {
      next.add(option.value)
      for (const o of options) if (o.parent === option.value) next.delete(o.value)
    }
    onChange(options.map((o) => o.value).filter((v) => next.has(v)))
  }

  return (
    <div className={styles.multi} ref={root}>
      <span className={styles.fieldLabel} id={`${id}-label`}>
        {label}
      </span>
      <button
        type="button"
        className={`${styles.multiButton} ${names.length > 0 ? styles.multiButtonActive : ''}`}
        aria-haspopup="true"
        aria-expanded={open}
        aria-labelledby={`${id}-label ${id}-value`}
        onClick={() => setOpen((o) => !o)}
      >
        <span id={`${id}-value`} className={styles.multiValue}>
          {summary}
        </span>
        <svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true">
          <path d="M3 4.5 6 7.5 9 4.5" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
        </svg>
      </button>
      {open && (
        <div className={styles.popover} role="group" aria-labelledby={`${id}-label`}>
          <div className={styles.popoverHead}>
            <span>{names.length === 0 ? placeholder : `${names.length} selected`}</span>
            <button type="button" className={styles.linkButton} disabled={names.length === 0} onClick={() => onChange([])}>
              Clear
            </button>
          </div>
          <div className={styles.popoverList}>
            {options.length === 0 && <p className={styles.popoverEmpty}>Nothing to choose from.</p>}
            {options.map((option) => {
              const covered = option.parent != null && chosen.has(option.parent)
              return (
                <label
                  key={option.value}
                  className={`${styles.option} ${option.parent != null ? styles.optionSub : styles.optionTop}`}
                >
                  <input
                    type="checkbox"
                    checked={covered || chosen.has(option.value)}
                    disabled={covered}
                    onChange={() => toggle(option)}
                  />
                  <span className={styles.optionLabel}>{option.label}</span>
                  {option.hint && <span className={styles.optionHint}>{option.hint}</span>}
                </label>
              )
            })}
          </div>
        </div>
      )}
    </div>
  )
}
