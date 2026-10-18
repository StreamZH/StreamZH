import { useEffect, useRef, useState, type ReactNode } from 'react'
import ui from '../components/ui.module.css'
import StatusIcon from '../status/icons'
import activity from './Activity.module.css'
import styles from './Filters.module.css'

const numberFormat = new Intl.NumberFormat()

/** The filters above a list: search, selects, the number of matches and "Clear filters" while a filter is set. */
export function FilterBar({
  label,
  active,
  matches,
  noun,
  onClear,
  children,
}: {
  label: string
  active: boolean
  /** Matching items; undefined while loading. */
  matches?: number
  /** Plural noun ("downloads"). */
  noun: string
  onClear: () => void
  children: ReactNode
}) {
  return (
    <div className={styles.bar} role="search" aria-label={label}>
      <div className={styles.controls}>{children}</div>
      {active && (
        <div className={styles.status}>
          <span className={styles.matches} aria-live="polite">
            {matches == null ? 'Filtering…' : `${numberFormat.format(matches)} matching ${matches === 1 ? noun.replace(/s$/, '') : noun}`}
          </span>
          <button type="button" className={styles.clear} onClick={onClear}>
            <StatusIcon name="x" size={13} /> Clear filters
          </button>
        </div>
      )}
    </div>
  )
}

/** Search box that applies what was typed once typing pauses; follows the value when it changes from outside (back/forward, Clear). */
export function SearchBox({ value, onChange, placeholder, label }: { value: string; onChange: (value: string) => void; placeholder: string; label: string }) {
  const [typed, setTyped] = useState(value)
  const sent = useRef(value)
  const latest = useRef(onChange)

  useEffect(() => {
    latest.current = onChange
  }, [onChange])

  useEffect(() => {
    if (value !== sent.current) {
      sent.current = value
      setTyped(value)
    }
  }, [value])

  useEffect(() => {
    const next = typed.trim()
    if (next === sent.current) return
    const timer = window.setTimeout(() => {
      sent.current = next
      latest.current(next)
    }, 300)
    return () => window.clearTimeout(timer)
  }, [typed])

  return (
    <div className={styles.search}>
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" aria-hidden="true">
        <circle cx="11" cy="11" r="6.5" />
        <path d="m16 16 4.5 4.5" />
      </svg>
      <input
        type="search"
        className={styles.searchInput}
        value={typed}
        placeholder={placeholder}
        aria-label={label}
        onChange={(e) => setTyped(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Escape' && typed) {
            e.preventDefault()
            setTyped('')
          }
        }}
      />
    </div>
  )
}

/** The filtered list is empty. */
export function NoMatches({ noun, onClear }: { noun: string; onClear: () => void }) {
  return (
    <div className={activity.empty}>
      <span className={activity.emptyIcon}>
        <StatusIcon name="info" size={22} />
      </span>
      <p className={activity.emptyTitle}>No {noun} match these filters</p>
      <p>Change the search or the filters, or clear them to see everything.</p>
      <div className={activity.emptyActions}>
        <button type="button" className={ui.button} onClick={onClear}>
          Clear filters
        </button>
      </div>
    </div>
  )
}

export interface FilterOption {
  value: string
  label: string
}

/** A filter select; the first option (value '') means "all". Highlighted while it filters. */
export function FilterSelect({
  label,
  value,
  onChange,
  options,
  groups,
  disabled,
}: {
  label: string
  value: string
  onChange: (value: string) => void
  options: FilterOption[]
  /** Extra option groups after the plain options (e.g. custom categories). */
  groups?: { label: string; options: FilterOption[] }[]
  disabled?: boolean
}) {
  const known = options.some((o) => o.value === value) || groups?.some((g) => g.options.some((o) => o.value === value))
  return (
    <select
      className={`${styles.select} ${value ? styles.selectActive : ''}`}
      value={value}
      aria-label={label}
      title={label}
      disabled={disabled}
      onChange={(e) => onChange(e.target.value)}
    >
      {options.map((o) => (
        <option key={o.value} value={o.value}>
          {o.label}
        </option>
      ))}
      {groups
        ?.filter((g) => g.options.length > 0)
        .map((g) => (
          <optgroup key={g.label} label={g.label}>
            {g.options.map((o) => (
              <option key={o.value} value={o.value}>
                {o.label}
              </option>
            ))}
          </optgroup>
        ))}
      {!known && value && <option value={value}>{value}</option>}
    </select>
  )
}
