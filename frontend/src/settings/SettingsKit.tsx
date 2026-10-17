import type { ReactNode } from 'react'
import ui from '../components/ui.module.css'
import { AdvancedHint, AdvancedToggle } from '../lib/Advanced'
import { advancedLabel, useAdvanced } from '../lib/advancedMode'
import { useScrollToHash } from '../lib/useScrollToHash'
import styles from './SettingsKit.module.css'

// Building blocks for every settings page, so all areas look and behave the same (like Radarr's settings):
// a page header with the Advanced switch, sections (cards) with label-left rows, and a save bar per section.

export function SettingsPage({ title, lead, actions, children }: { title: string; lead?: ReactNode; actions?: ReactNode; children: ReactNode }) {
  useScrollToHash()
  return (
    <div className={styles.page}>
      <header className={styles.header}>
        <div>
          <div className={styles.eyebrow}>Settings</div>
          <h1 className={styles.title}>{title}</h1>
          {lead && <p className={styles.lead}>{lead}</p>}
        </div>
        <div className={styles.headerActions}>
          {actions}
          <AdvancedToggle />
        </div>
      </header>
      {children}
    </div>
  )
}

/**
 * A card with a title. `advanced`: the whole section is an expert setting (hidden unless advanced mode is on).
 * `footer`: usually <SaveBar/>.
 */
export function Section({
  id,
  title,
  description,
  advanced = false,
  hiddenCount = 0,
  footer,
  children,
}: {
  id?: string
  title: string
  description?: ReactNode
  advanced?: boolean
  /** Number of expert rows in this section, for the "n expert settings hidden" line. */
  hiddenCount?: number
  footer?: ReactNode
  children: ReactNode
}) {
  const advancedMode = useAdvanced()
  if (advanced && !advancedMode) return null
  return (
    <section id={id} className={styles.section}>
      <div className={styles.sectionHead}>
        <h2 className={`${styles.sectionTitle} ${advanced ? advancedLabel : ''}`}>{title}</h2>
        {description && <p className={styles.sectionText}>{description}</p>}
      </div>
      <div className={styles.rows}>{children}</div>
      <AdvancedHint count={hiddenCount} />
      {footer && <div className={styles.footer}>{footer}</div>}
    </section>
  )
}

/**
 * One setting: label on the left, control and help on the right. `advanced` rows only show in advanced mode,
 * or while they have an error (so nobody gets stuck on a hidden field).
 */
export function Row({
  label,
  htmlFor,
  help,
  error,
  advanced = false,
  children,
}: {
  label: string
  htmlFor?: string
  help?: ReactNode
  error?: string
  advanced?: boolean
  children: ReactNode
}) {
  const advancedMode = useAdvanced()
  if (advanced && !advancedMode && !error) return null
  const labelClass = `${styles.label} ${advanced ? advancedLabel : ''}`
  return (
    <div className={`${styles.row} ${error ? styles.invalid : ''}`}>
      {htmlFor ? (
        <label className={labelClass} htmlFor={htmlFor}>
          {label}
        </label>
      ) : (
        <span className={labelClass}>{label}</span>
      )}
      <div className={styles.control}>
        {children}
        {help && <small className={styles.help}>{help}</small>}
        {error && <small className={styles.error}>{error}</small>}
      </div>
    </div>
  )
}

export function Toggle({
  checked,
  onChange,
  on = 'On',
  off = 'Off',
  disabled,
}: {
  checked: boolean
  onChange: (checked: boolean) => void
  on?: string
  off?: string
  disabled?: boolean
}) {
  return (
    <label className={styles.switch}>
      <input type="checkbox" role="switch" checked={checked} disabled={disabled} onChange={(e) => onChange(e.target.checked)} />
      <span className={styles.track} aria-hidden="true" />
      <span>{checked ? on : off}</span>
    </label>
  )
}

/** Number input with a unit after it ("days", "MB", "minutes"). Empty input gives null. */
export function NumberInput({
  id,
  value,
  onChange,
  unit,
  min,
  max,
  step,
  placeholder,
  wide = false,
}: {
  id?: string
  value: number | null
  onChange: (value: number | null) => void
  unit?: string
  min?: number
  max?: number
  step?: number
  placeholder?: string
  wide?: boolean
}) {
  return (
    <div className={styles.withUnit}>
      <input
        id={id}
        className={`${ui.input} ${wide ? styles.numberWide : styles.number}`}
        type="number"
        inputMode="decimal"
        min={min}
        max={max}
        step={step}
        placeholder={placeholder}
        value={value == null || Number.isNaN(value) ? '' : value}
        onChange={(e) => onChange(e.target.value === '' ? null : e.target.valueAsNumber)}
      />
      {unit && <span className={styles.unit}>{unit}</span>}
    </div>
  )
}

export function Select<V extends string>({
  id,
  value,
  onChange,
  options,
}: {
  id?: string
  value: V
  onChange: (value: V) => void
  options: readonly { value: V; label: string }[]
}) {
  return (
    <select id={id} className={`${ui.select} ${styles.select}`} value={value} onChange={(e) => onChange(e.target.value as V)}>
      {options.map((o) => (
        <option key={o.value} value={o.value}>
          {o.label}
        </option>
      ))}
    </select>
  )
}

/** Save and discard for one section. Shows "Unsaved changes", "Saving…", "Saved" and a form-level error. */
export function SaveBar({
  dirty,
  saving,
  saved,
  error,
  onSave,
  onReset,
  disabled = false,
}: {
  dirty: boolean
  saving: boolean
  saved: boolean
  error?: string | null
  onSave: () => void
  onReset: () => void
  disabled?: boolean
}) {
  return (
    <div className={styles.saveBar}>
      <span className={styles.saveState} aria-live="polite">
        {error ? <span className={ui.error}>{error}</span> : saving ? 'Saving…' : saved ? <span className={ui.ok}>Saved</span> : dirty ? 'Unsaved changes' : null}
      </span>
      {dirty && (
        <button className={ui.button} type="button" onClick={onReset} disabled={saving}>
          Discard
        </button>
      )}
      <button className={ui.primary} type="button" onClick={onSave} disabled={!dirty || saving || disabled}>
        {saving ? 'Saving…' : 'Save'}
      </button>
    </div>
  )
}

/** Placeholder for an area that is still being built. */
export function ComingSoon({ children }: { children: ReactNode }) {
  return <div className={styles.coming}>{children}</div>
}
