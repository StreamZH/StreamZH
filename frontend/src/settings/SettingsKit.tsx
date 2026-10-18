import { createContext, useContext, useId, type ReactNode } from 'react'
import Icon from '../components/Icon'
import PageHeader from '../components/PageHeader'
import ui from '../components/ui.module.css'
import { AdvancedHint, AdvancedToggle } from '../lib/Advanced'
import { advancedLabel, useAdvanced } from '../lib/advancedMode'
import { useScrollToHash } from '../lib/useScrollToHash'
import styles from './SettingsKit.module.css'

// Building blocks for every settings page, so all areas look and behave the same (CloudZH settings: .set-sec,
// .set-row, .switch): a page head with the Advanced switch, sections (panels) with rows split by hairlines, label and
// help on the left, the control on the right, and a save bar that rises in at the bottom while a form is dirty.

export function SettingsPage({
  title,
  lead,
  actions,
  eyebrow = 'Settings',
  advancedToggle = true,
  children,
}: {
  title: string
  lead?: ReactNode
  actions?: ReactNode
  eyebrow?: ReactNode
  /** Pages without expert settings leave the Advanced switch out. */
  advancedToggle?: boolean
  children: ReactNode
}) {
  useScrollToHash()
  return (
    <div className={styles.page}>
      <PageHeader
        eyebrow={eyebrow}
        title={title}
        lead={lead}
        actions={
          <>
            {actions}
            {advancedToggle && <AdvancedToggle />}
          </>
        }
      />
      {children}
    </div>
  )
}

/**
 * A panel with a title. `advanced`: the whole section is an expert setting (hidden unless advanced mode is on).
 * `footer`: usually <SaveBar/>.
 */
export function Section({
  id,
  title,
  description,
  advanced = false,
  hiddenCount = 0,
  actions,
  footer,
  children,
}: {
  id?: string
  title: string
  description?: ReactNode
  advanced?: boolean
  /** Number of expert rows in this section, for the "n expert settings hidden" line. */
  hiddenCount?: number
  /** Small buttons on the right of the title. */
  actions?: ReactNode
  footer?: ReactNode
  children: ReactNode
}) {
  const advancedMode = useAdvanced()
  if (advanced && !advancedMode) return null
  return (
    <section id={id} className={`${ui.panel} ${styles.section}`}>
      <div className={styles.sectionHead}>
        <div className={styles.sectionText}>
          <h2 className={`${styles.sectionTitle} ${advanced ? advancedLabel : ''}`}>{title}</h2>
          {description && <p className={styles.sectionDesc}>{description}</p>}
        </div>
        {actions && <div className={styles.sectionActions}>{actions}</div>}
      </div>
      <div className={styles.rows}>{children}</div>
      <AdvancedHint count={hiddenCount} />
      {footer && <div className={styles.footer}>{footer}</div>}
    </section>
  )
}

/** Id of the row label, so a Toggle inside a row is named by it. */
const RowLabel = createContext<string | undefined>(undefined)

/**
 * One setting: label and help on the left, the control on the right (below on narrow screens). `advanced` rows only
 * show in advanced mode, or while they have an error (so nobody gets stuck on a hidden field).
 */
export function Row({
  label,
  htmlFor,
  help,
  error,
  advanced = false,
  wide = false,
  children,
}: {
  label: string
  htmlFor?: string
  help?: ReactNode
  error?: string
  advanced?: boolean
  /** The control needs the whole width (lists, editors): it goes below the label. */
  wide?: boolean
  children: ReactNode
}) {
  const advancedMode = useAdvanced()
  const labelId = useId()
  if (advanced && !advancedMode && !error) return null
  const labelClass = `${styles.label} ${advanced ? advancedLabel : ''}`
  return (
    <div className={`${styles.row} ${wide ? styles.rowWide : ''} ${error ? styles.invalid : ''}`}>
      <div className={styles.rowText}>
        {htmlFor ? (
          <label id={labelId} className={labelClass} htmlFor={htmlFor}>
            {label}
          </label>
        ) : (
          <span id={labelId} className={labelClass}>
            {label}
          </span>
        )}
        {help && <small className={styles.help}>{help}</small>}
      </div>
      <div className={styles.control}>
        <RowLabel.Provider value={labelId}>{children}</RowLabel.Provider>
        {error && (
          <small className={styles.error} role="alert">
            <Icon name="alert" size={13} />
            {error}
          </small>
        )}
      </div>
    </div>
  )
}

/** CloudZH switch. `on`/`off`: an optional state text next to it (none by default). */
export function Toggle({
  checked,
  onChange,
  on,
  off,
  disabled,
  label,
}: {
  checked: boolean
  onChange: (checked: boolean) => void
  on?: string
  off?: string
  disabled?: boolean
  /** Accessible name when the switch is not inside a Row. */
  label?: string
}) {
  const rowLabel = useContext(RowLabel)
  const text = checked ? on : off
  return (
    <label className={`${styles.toggle} ${disabled ? styles.toggleOff : ''}`}>
      <span className={ui.switch}>
        <input
          type="checkbox"
          role="switch"
          checked={checked}
          disabled={disabled}
          aria-label={label}
          aria-labelledby={label ? undefined : rowLabel}
          onChange={(e) => onChange(e.target.checked)}
        />
        <span aria-hidden="true" />
      </span>
      {text && <span className={styles.toggleText}>{text}</span>}
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
        className={`${ui.input} ${ui.num} ${wide ? styles.numberWide : styles.number}`}
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

/**
 * Save and discard for one form: a bar that rises in at the bottom of the screen while there are unsaved changes,
 * shows "Saving…", a form-level error, and "Saved" with a check that fades out.
 */
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
  const active = dirty || saving || !!error
  const show = active || saved
  return (
    <div className={`${styles.saveSlot} ${show ? styles.saveSlotOn : ''}`} aria-live="polite">
      {show && (
        <div className={`${styles.saveBar} ${!active ? styles.saveBarDone : ''}`}>
          <span className={styles.saveState}>
            {error ? (
              <span className={styles.saveError}>
                <Icon name="alert" size={15} />
                {error}
              </span>
            ) : saving ? (
              <>
                <span className={ui.dotBusy} />
                Saving…
              </>
            ) : !active ? (
              <span className={styles.saveDone}>
                <Icon name="check" size={15} />
                Saved
              </span>
            ) : (
              <>
                <span className={ui.dotWarn} />
                Unsaved changes
              </>
            )}
          </span>
          {active && (
            <div className={styles.saveActions}>
              {(dirty || error) && (
                <button className={ui.ghost} type="button" onClick={onReset} disabled={saving}>
                  Discard
                </button>
              )}
              <button className={ui.primary} type="button" onClick={onSave} disabled={!dirty || saving || disabled}>
                <Icon name="check" size={15} />
                {saving ? 'Saving…' : 'Save'}
              </button>
            </div>
          )}
        </div>
      )}
    </div>
  )
}

/** Placeholder for an area that is still being built. */
export function ComingSoon({ children }: { children: ReactNode }) {
  return <div className={styles.coming}>{children}</div>
}
