import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useId, useState, type FormEvent, type ReactNode } from 'react'
import { ApiError } from '../api/client'
import {
  connectApi,
  connectKeys,
  maskedSecret,
  triggerKeys,
  type Connection,
  type ConnectField,
  type ConnectionTriggers,
  type ConnectType,
  type SettingValue,
} from '../api/connect'
import ui from '../components/ui.module.css'
import Dialog from '../indexers/Dialog'
import form from '../indexers/Form.module.css'
import { AdvancedHint, AdvancedToggle } from '../lib/Advanced'
import { advancedLabel, setAdvanced, useAdvanced } from '../lib/advancedMode'
import { triggerHelp, triggerLabels } from './labels'
import styles from './Connect.module.css'

const topFields = ['name', 'enable']

export function Field({
  id,
  label,
  advanced = false,
  help,
  error,
  children,
}: {
  id?: string
  label: string
  advanced?: boolean
  help?: ReactNode
  error?: string
  children: ReactNode
}) {
  const labelClass = `${form.label} ${advanced ? advancedLabel : ''}`
  return (
    <div className={`${form.row} ${error ? form.invalid : ''}`}>
      {id ? (
        <label className={labelClass} htmlFor={id}>
          {label}
        </label>
      ) : (
        <span className={labelClass}>{label}</span>
      )}
      <div className={form.control}>
        {children}
        {help && <small className={form.help}>{help}</small>}
        {error && <small className={form.fieldError}>{error}</small>}
      </div>
    </div>
  )
}

export function Switch({ checked, onChange, on = 'On', off = 'Off', label }: { checked: boolean; onChange: (checked: boolean) => void; on?: string; off?: string; label?: string }) {
  return (
    <label className={form.switch}>
      <input type="checkbox" role="switch" checked={checked} aria-label={label} onChange={(e) => onChange(e.target.checked)} />
      <span className={form.track} aria-hidden="true" />
      <span>{checked ? on : off}</span>
    </label>
  )
}

/** Add or edit one connection: name, triggers and the type's own fields, with Test, Save and Delete. */
export default function ConnectionForm({
  connection,
  type,
  canEdit,
  onClose,
  onBack,
}: {
  connection: Connection
  type: ConnectType
  canEdit: boolean
  onClose: () => void
  /** New connections: back to the type list. */
  onBack?: () => void
}) {
  const queryClient = useQueryClient()
  const formId = useId()
  const advanced = useAdvanced()
  const [draft, setDraft] = useState<Connection>(connection)
  const [edited, setEdited] = useState<Set<string>>(new Set())
  const [confirmDelete, setConfirmDelete] = useState(false)
  const isNew = connection.id === 0

  // A save or test error on a hidden expert field switches advanced mode on, so the error is visible.
  const showHidden = (error: Error) => {
    if (error instanceof ApiError && type.fields.some((f) => f.advanced && error.fieldErrors[f.name])) setAdvanced(true)
  }

  const done = async () => {
    await queryClient.invalidateQueries({ queryKey: connectKeys.all })
    onClose()
  }
  const save = useMutation({ mutationFn: () => (isNew ? connectApi.create(draft) : connectApi.update(draft)), onSuccess: done, onError: showHidden })
  const remove = useMutation({ mutationFn: () => connectApi.remove(connection.id), onSuccess: done })
  const test = useMutation({
    mutationFn: () => connectApi.test(draft),
    onError: showHidden,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: connectKeys.list }),
  })

  const saveErrors = save.error instanceof ApiError ? save.error.fieldErrors : {}
  const testErrors = test.error instanceof ApiError ? test.error.fieldErrors : {}
  const fieldError = (name: string) => (edited.has(name) ? undefined : (saveErrors[name] ?? testErrors[name])?.[0])
  const openErrors = [...Object.keys(saveErrors), ...Object.keys(testErrors)].some((name) => !edited.has(name))

  const update = (change: Partial<Connection>) => {
    setDraft((d) => ({ ...d, ...change }))
    setEdited((e) => new Set([...e, ...Object.keys(change)]))
  }
  const setting = (name: string, value: SettingValue) => {
    setDraft((d) => ({ ...d, settings: { ...d.settings, [name]: value } }))
    setEdited((e) => new Set([...e, name]))
  }

  const submit = (event: FormEvent) => {
    event.preventDefault()
    setEdited(new Set())
    test.reset()
    save.mutate()
  }

  const id = (name: string) => `${formId}-${name}`
  const health = type.triggers.includes('healthIssue') || type.triggers.includes('healthRestored')
  const hiddenCount = type.fields.filter((f) => f.advanced).length
  const testError = test.error && Object.keys(testErrors).length === 0 ? test.error.message : null

  const footer = canEdit ? (
    confirmDelete ? (
      <>
        <span className={form.confirm}>Delete {connection.name}?</span>
        <span className={form.spacer} />
        <button className={ui.button} type="button" onClick={() => setConfirmDelete(false)}>
          Keep
        </button>
        <button className={ui.danger} type="button" onClick={() => remove.mutate()} disabled={remove.isPending}>
          {remove.isPending ? 'Deleting…' : 'Yes, delete'}
        </button>
      </>
    ) : (
      <>
        <div className={form.testResult} aria-live="polite">
          {test.isPending && <span className={ui.muted}>Testing…</span>}
          {test.isSuccess && <span className={ui.ok}>{test.data.message}</span>}
          {testError && <span className={ui.error}>{testError}</span>}
          {save.error && Object.keys(saveErrors).length === 0 && <span className={ui.error}>{save.error.message}</span>}
          {openErrors && <span className={ui.error}>Check the highlighted fields.</span>}
          {remove.error && <span className={ui.error}>{remove.error.message}</span>}
        </div>
        {onBack && (
          <button className={ui.button} type="button" onClick={onBack}>
            Back
          </button>
        )}
        {!isNew && (
          <button className={`${ui.button} ${form.delete}`} type="button" onClick={() => setConfirmDelete(true)}>
            Delete
          </button>
        )}
        <span className={form.spacer} />
        <button
          className={ui.button}
          type="button"
          onClick={() => {
            setEdited(new Set())
            save.reset()
            test.mutate()
          }}
          disabled={test.isPending}
        >
          {test.isPending ? 'Testing…' : 'Test'}
        </button>
        <button className={ui.button} type="button" onClick={onClose}>
          Cancel
        </button>
        <button className={ui.primary} type="submit" form={formId} disabled={save.isPending}>
          {save.isPending ? 'Saving…' : 'Save'}
        </button>
      </>
    )
  ) : (
    <>
      <span className={form.spacer} />
      <button className={ui.button} type="button" onClick={onClose}>
        Close
      </button>
    </>
  )

  return (
    <Dialog
      title={isNew ? `Add ${type.name}` : canEdit ? `Edit ${connection.name}` : connection.name}
      subtitle={
        <>
          {type.description}
          {type.infoUrl && (
            <>
              {' '}
              <a href={type.infoUrl} target="_blank" rel="noreferrer">
                How to set it up
              </a>
            </>
          )}
        </>
      }
      onClose={onClose}
      footer={footer}
      headerAction={<AdvancedToggle />}
    >
      <form id={formId} className={form.form} onSubmit={submit} noValidate>
        {!isNew && connection.enable && connection.status.failing && (
          <p className={form.statusNote}>Last delivery failed: {connection.status.lastError}</p>
        )}
        <fieldset className={form.fieldset} disabled={!canEdit}>
          <Field id={id('name')} label="Name" help="Shown in the list, the status window and the log." error={fieldError('name')}>
            <input id={id('name')} className={ui.input} value={draft.name} maxLength={100} onChange={(e) => update({ name: e.target.value })} autoFocus />
          </Field>

          <Field label="Enable" help="Off: StreamZH never contacts this service." error={fieldError('enable')}>
            <Switch checked={draft.enable} onChange={(enable) => update({ enable })} on="Enabled" off="Disabled" />
          </Field>

          <Field label="Triggers" help="When StreamZH contacts this service.">
            <div className={styles.triggerChecks}>
              {type.triggers.map((t) => (
                <label key={t} className={styles.triggerCheck} title={triggerHelp[t]}>
                  <input type="checkbox" checked={draft[triggerKeys[t]]} onChange={(e) => update({ [triggerKeys[t]]: e.target.checked } as Partial<ConnectionTriggers>)} />
                  <span>
                    {triggerLabels[t]}
                    <small>{triggerHelp[t]}</small>
                  </span>
                </label>
              ))}
            </div>
          </Field>

          {health && (
            <Field
              label="Include health warnings"
              help="Health triggers also fire for warnings (e.g. low disk space, an indexer paused), not only for errors."
              error={fieldError('includeHealthWarnings')}
            >
              <Switch checked={draft.includeHealthWarnings} onChange={(includeHealthWarnings) => update({ includeHealthWarnings })} />
            </Field>
          )}

          {type.fields
            .filter((f) => advanced || !f.advanced || fieldError(f.name))
            .filter((f) => !topFields.includes(f.name))
            .map((f) => (
              <SettingField key={f.name} id={id(f.name)} field={f} value={draft.settings[f.name] ?? null} error={fieldError(f.name)} onChange={(v) => setting(f.name, v)} />
            ))}
        </fieldset>
        <AdvancedHint count={hiddenCount} />
      </form>
    </Dialog>
  )
}

/** One type-specific setting, drawn from its field definition. */
export function SettingField({
  id,
  field: f,
  value,
  error,
  onChange,
}: {
  id: string
  field: ConnectField
  value: SettingValue
  error?: string
  onChange: (value: SettingValue) => void
}) {
  const text = typeof value === 'string' ? value : value == null ? '' : String(value)
  let control: ReactNode
  let help: ReactNode = f.help

  switch (f.type) {
    case 'bool':
      control = <Switch checked={value === true} onChange={onChange} label={f.label} />
      break
    case 'number':
      control = (
        <div className={styles.withUnit}>
          <input
            id={id}
            className={`${ui.input} ${form.number}`}
            type="number"
            inputMode="numeric"
            min={f.min ?? undefined}
            max={f.max ?? undefined}
            placeholder={f.placeholder ?? undefined}
            value={typeof value === 'number' ? value : ''}
            onChange={(e) => onChange(e.target.value === '' ? null : e.target.valueAsNumber)}
          />
          {f.unit && <span className={styles.unit}>{f.unit}</span>}
        </div>
      )
      break
    case 'select':
      control = (
        <select id={id} className={ui.select} value={text} onChange={(e) => onChange(e.target.value)}>
          {(f.options ?? []).map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
      )
      break
    case 'multiSelect': {
      const selected = Array.isArray(value) ? value : []
      control = (
        <div className={styles.chips} role="group" aria-label={f.label}>
          {(f.options ?? []).map((o) => {
            const on = selected.includes(o.value)
            return (
              <button
                key={o.value}
                type="button"
                className={`${styles.chip} ${on ? styles.chipOn : ''}`}
                aria-pressed={on}
                onClick={() => onChange(on ? selected.filter((s) => s !== o.value) : [...selected, o.value])}
              >
                {o.label}
              </button>
            )
          })}
        </div>
      )
      break
    }
    case 'textArea': {
      // Secret text (custom headers, 0.12): shown masked like a password; typing replaces it.
      const saved = f.secret === true && text === maskedSecret
      if (saved) {
        help = (
          <>
            {f.help}
            {f.help && ' '}
            Saved. Type new ones to replace them.
          </>
        )
      }
      control = (
        <textarea
          id={id}
          className={`${ui.input} ${ui.mono} ${styles.textArea}`}
          rows={3}
          spellCheck={false}
          autoComplete="off"
          placeholder={f.placeholder ?? undefined}
          value={text}
          onFocus={(e) => saved && e.target.select()}
          onChange={(e) => onChange(e.target.value)}
        />
      )
      break
    }
    case 'password': {
      const saved = text === maskedSecret
      help = (
        <>
          {f.help}
          {f.help && ' '}
          {saved ? 'Saved. Type a new one to replace it.' : ''}
        </>
      )
      control = (
        <input
          id={id}
          className={`${ui.input} ${ui.mono}`}
          type="password"
          autoComplete="new-password"
          spellCheck={false}
          placeholder={f.placeholder ?? undefined}
          value={text}
          onFocus={(e) => saved && e.target.select()}
          onChange={(e) => onChange(e.target.value)}
        />
      )
      break
    }
    default:
      control = (
        <input
          id={id}
          className={`${ui.input} ${f.type === 'url' ? ui.mono : ''}`}
          inputMode={f.type === 'url' ? 'url' : undefined}
          autoComplete="off"
          autoCapitalize="off"
          spellCheck={false}
          placeholder={f.placeholder ?? (f.type === 'list' ? 'one, two' : undefined)}
          value={text}
          onChange={(e) => onChange(e.target.value)}
        />
      )
  }

  return (
    <Field id={f.type === 'bool' || f.type === 'multiSelect' ? undefined : id} label={f.label} advanced={f.advanced} help={help} error={error}>
      {control}
    </Field>
  )
}
