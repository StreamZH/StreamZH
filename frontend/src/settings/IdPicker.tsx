import { useState, type KeyboardEvent } from 'react'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import styles from './Discord.module.css'

export interface IdOption {
  id: string
  label: string
  /** Second line in the menu, e.g. the server of a channel. */
  group?: string
}

const snowflake = /^\d{17,20}$/

/**
 * Discord ids as chips: pick from what the bot sees (after a test) or paste an id. The chips show names where known,
 * else the id. `single`: at most one id (the picker replaces it). Empty shows `emptyText`.
 */
export function IdPicker({
  id,
  value,
  onChange,
  options,
  emptyText,
  addLabel,
  single = false,
  prefix = '',
}: {
  id: string
  value: string[]
  onChange: (value: string[]) => void
  options: IdOption[]
  emptyText: string
  addLabel: string
  single?: boolean
  /** Shown before names: "#" for channels, "@" for roles. */
  prefix?: string
}) {
  const [text, setText] = useState('')
  const [invalid, setInvalid] = useState(false)
  const known = new Map(options.map((o) => [o.id, o]))
  const free = options.filter((o) => !value.includes(o.id))

  const add = (raw: string) => {
    const ids = raw
      .split(/[\s,]+/)
      .map((t) => t.trim())
      .filter(Boolean)
    if (ids.length === 0) return
    if (ids.some((t) => !snowflake.test(t))) {
      setInvalid(true)
      return
    }
    const next = single ? [ids[ids.length - 1]] : [...value, ...ids.filter((t) => !value.includes(t))]
    onChange(next)
    setText('')
    setInvalid(false)
  }

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') {
      e.preventDefault()
      add(text)
    }
  }

  return (
    <div className={styles.picker}>
      <div className={styles.chips} aria-live="polite">
        {value.length === 0 && <span className={styles.chipEmpty}>{emptyText}</span>}
        {value.map((v) => {
          const option = known.get(v)
          return (
            <span key={v} className={styles.chip} title={v}>
              {option ? (
                <>
                  {prefix}
                  {option.label}
                  {option.group && <span className={styles.chipGroup}>{option.group}</span>}
                </>
              ) : (
                <span className={ui.mono}>{v}</span>
              )}
              <button type="button" className={styles.chipRemove} onClick={() => onChange(value.filter((x) => x !== v))} aria-label={`Remove ${option?.label ?? v}`}>
                <Icon name="x" size={12} />
              </button>
            </span>
          )
        })}
      </div>
      <div className={styles.pickerAdd}>
        {free.length > 0 && (
          <select
            id={id}
            className={`${ui.select} ${styles.pickerSelect}`}
            value=""
            aria-label={addLabel}
            onChange={(e) => e.target.value && add(e.target.value)}
          >
            <option value="">{addLabel}…</option>
            {free.map((o) => (
              <option key={o.id} value={o.id}>
                {prefix}
                {o.label}
                {o.group ? ` · ${o.group}` : ''}
              </option>
            ))}
          </select>
        )}
        <input
          id={free.length > 0 ? undefined : id}
          className={`${ui.input} ${ui.mono} ${styles.pickerInput}`}
          inputMode="numeric"
          spellCheck={false}
          autoComplete="off"
          placeholder="Paste an id"
          aria-label={`${addLabel} by id`}
          aria-invalid={invalid}
          value={text}
          onChange={(e) => {
            setText(e.target.value)
            setInvalid(false)
          }}
          onKeyDown={onKeyDown}
          onBlur={() => text.trim() && add(text)}
        />
      </div>
      {invalid && <small className={ui.error}>Ids are numbers of 17 to 20 digits (Discord: Developer Mode on, then right-click → Copy ID).</small>}
    </div>
  )
}
