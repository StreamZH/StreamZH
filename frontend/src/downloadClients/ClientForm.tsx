import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useId, useState, type FormEvent, type ReactNode } from 'react'
import { ApiError } from '../api/client'
import {
  builtInImplementation,
  downloadClientKeys,
  downloadClientsApi,
  maskedSecret,
  type ClientField,
  type ClientImplementation,
  type ClientValue,
  type DownloadClient,
} from '../api/downloadClients'
import FolderPicker from '../components/FolderPicker'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import { Field, Switch } from '../connect/ConnectionForm'
import Dialog from '../indexers/Dialog'
import form from '../indexers/Form.module.css'
import { AdvancedHint, AdvancedToggle } from '../lib/Advanced'
import { setAdvanced, useAdvanced } from '../lib/advancedMode'
import { ProtocolTag } from './torrent'
import styles from './DownloadClients.module.css'

/** Add or edit one download client: name, enable, priority, remove completed, then the implementation's own fields from the schema. */
export default function ClientForm({
  client,
  type,
  canEdit,
  onClose,
  onBack,
}: {
  client: DownloadClient
  type: ClientImplementation
  canEdit: boolean
  onClose: () => void
  /** New clients: back to the implementation picker. */
  onBack?: () => void
}) {
  const queryClient = useQueryClient()
  const formId = useId()
  const advanced = useAdvanced()
  const [draft, setDraft] = useState<DownloadClient>(client)
  const [edited, setEdited] = useState<Set<string>>(new Set())
  const [confirmDelete, setConfirmDelete] = useState(false)
  const isNew = client.id === 0
  const builtIn = type.implementation === builtInImplementation

  // A save or test error on a hidden expert field switches advanced mode on, so the error is visible.
  const expertNames = new Set(['priority', ...type.fields.filter((f) => f.advanced).map((f) => f.name)])
  const showHidden = (error: Error) => {
    if (error instanceof ApiError && Object.keys(error.fieldErrors).some((k) => expertNames.has(fieldKey(k)))) setAdvanced(true)
  }

  const done = async () => {
    await queryClient.invalidateQueries({ queryKey: downloadClientKeys.all })
    onClose()
  }
  const save = useMutation({ mutationFn: () => (isNew ? downloadClientsApi.create(draft) : downloadClientsApi.update(draft)), onSuccess: done, onError: showHidden })
  const remove = useMutation({ mutationFn: () => downloadClientsApi.remove(client.id), onSuccess: done })
  const test = useMutation({ mutationFn: () => downloadClientsApi.test(draft), onError: showHidden })

  const saveErrors = errorsByField(save.error)
  const testErrors = errorsByField(test.error)
  const fieldError = (name: string) => (edited.has(name) ? undefined : (saveErrors[name] ?? testErrors[name])?.[0])
  const openErrors = [...Object.keys(saveErrors), ...Object.keys(testErrors)].some((name) => !edited.has(name))

  const update = (change: Partial<DownloadClient>) => {
    setDraft((d) => ({ ...d, ...change }))
    setEdited((e) => new Set([...e, ...Object.keys(change)]))
    test.reset()
  }
  const setting = (name: string, value: ClientValue) => {
    setDraft((d) => ({ ...d, settings: { ...d.settings, [name]: value } }))
    setEdited((e) => new Set([...e, name]))
    test.reset()
  }

  const submit = (event: FormEvent) => {
    event.preventDefault()
    setEdited(new Set())
    test.reset()
    save.mutate()
  }

  const id = (name: string) => `${formId}-${name}`
  const shownFields = type.fields.filter((f) => advanced || !f.advanced || fieldError(f.name))
  const hiddenCount = type.fields.filter((f) => f.advanced).length + 1
  const testError = test.error && Object.keys(testErrors).length === 0 ? test.error.message : null

  const footer = canEdit ? (
    confirmDelete ? (
      <>
        <span className={form.confirm}>Delete {client.name}? Torrents in it keep running there.</span>
        <span className={form.spacer} />
        <button className={ui.button} type="button" onClick={() => setConfirmDelete(false)}>
          Keep
        </button>
        <button className={ui.dangerSolid} type="button" onClick={() => remove.mutate()} disabled={remove.isPending}>
          {remove.isPending ? 'Deleting…' : 'Yes, delete'}
        </button>
      </>
    ) : (
      <>
        <div className={form.testResult} aria-live="polite">
          {test.isPending && <span className={ui.muted}>Testing the connection…</span>}
          {test.isSuccess && (
            <span className={`${styles.testMsg} ${test.data.ok ? styles.testOk : ui.error}`}>
              <Icon name={test.data.ok ? 'check' : 'alert'} size={14} />
              {test.data.message}
            </span>
          )}
          {testError && (
            <span className={`${styles.testMsg} ${ui.error}`}>
              <Icon name="alert" size={14} />
              {testError}
            </span>
          )}
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
          <button className={ui.danger} type="button" onClick={() => setConfirmDelete(true)}>
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
      title={isNew ? `Add ${type.name}` : canEdit ? `Edit ${client.name}` : client.name}
      subtitle={
        <span className={styles.formSub}>
          <ProtocolTag protocol={type.protocol} />
          <span>{type.description}</span>
          {type.infoUrl && (
            <a href={type.infoUrl} target="_blank" rel="noreferrer noopener">
              Website
            </a>
          )}
        </span>
      }
      onClose={onClose}
      footer={footer}
      headerAction={<AdvancedToggle />}
    >
      <form id={formId} className={form.form} onSubmit={submit} noValidate>
        {!isNew && client.enable && client.status && !client.status.healthy && client.status.message && (
          <p className={form.statusNote}>Last check failed: {client.status.message}</p>
        )}
        <fieldset className={form.fieldset} disabled={!canEdit}>
          <Field id={id('name')} label="Name" help="Shown in Activity and the status window." error={fieldError('name')}>
            <input id={id('name')} className={ui.input} value={draft.name} maxLength={100} onChange={(e) => update({ name: e.target.value })} autoFocus />
          </Field>

          <Field label="Enable" help="Off: StreamZH sends nothing to this client and stops watching it." error={fieldError('enable')}>
            <Switch checked={draft.enable} onChange={(enable) => update({ enable })} on="Enabled" off="Disabled" label="Enable" />
          </Field>

          {shownFields.map((f) => (
            <ClientSettingField key={f.name} id={id(f.name)} field={f} value={draft.settings[f.name] ?? null} error={fieldError(f.name)} browse={builtIn} onChange={(v) => setting(f.name, v)} />
          ))}

          <Field
            label="Remove completed"
            help="After import, remove the torrent and its data from the client once the seed goals are met (the indexer's seed ratio and time, else the client's own)."
            error={fieldError('removeCompleted')}
          >
            <Switch checked={draft.removeCompleted} onChange={(removeCompleted) => update({ removeCompleted })} label="Remove completed" />
          </Field>

          {(advanced || fieldError('priority')) && (
            <Field id={id('priority')} label="Client priority" advanced help="1 to 50, 1 is the highest. New torrents go to the enabled client with the highest priority." error={fieldError('priority')}>
              <input
                id={id('priority')}
                className={`${ui.input} ${form.number}`}
                type="number"
                min={1}
                max={50}
                inputMode="numeric"
                value={Number.isNaN(draft.priority) ? '' : draft.priority}
                onChange={(e) => update({ priority: e.target.valueAsNumber })}
              />
            </Field>
          )}
        </fieldset>
        <AdvancedHint count={hiddenCount} />
      </form>
    </Dialog>
  )
}

/** "settings.host" or "Settings.Host" from the server → "host", so errors land on the right field. */
function fieldKey(key: string): string {
  const last = key.split('.').pop() ?? key
  return last.charAt(0).toLowerCase() + last.slice(1)
}

function errorsByField(error: Error | null): Record<string, string[]> {
  if (!(error instanceof ApiError)) return {}
  const result: Record<string, string[]> = {}
  for (const [key, messages] of Object.entries(error.fieldErrors)) result[fieldKey(key)] = messages
  return result
}

/** One implementation setting, drawn from its schema field. */
function ClientSettingField({
  id,
  field: f,
  value,
  error,
  browse,
  onChange,
}: {
  id: string
  field: ClientField
  value: ClientValue
  error?: string
  /** Paths on this machine (built-in engine): offer the folder browser. */
  browse: boolean
  onChange: (value: ClientValue) => void
}) {
  const text = value == null ? '' : String(value)
  let control: ReactNode
  let help: ReactNode = f.helpText

  switch (f.type) {
    case 'checkbox':
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
            placeholder={f.placeholder ?? undefined}
            value={typeof value === 'number' ? value : text}
            onChange={(e) => onChange(e.target.value === '' ? null : e.target.valueAsNumber)}
          />
          {f.unit && <span className={styles.unit}>{f.unit}</span>}
        </div>
      )
      break
    case 'select': {
      const options = f.options ?? []
      const numeric = options.length > 0 && options.every((o) => typeof o.value === 'number')
      control = (
        <select id={id} className={`${ui.select} ${styles.select}`} value={text} onChange={(e) => onChange(numeric ? Number(e.target.value) : e.target.value)}>
          {!options.some((o) => String(o.value) === text) && <option value={text}>{text || 'Choose…'}</option>}
          {options.map((o) => (
            <option key={String(o.value)} value={String(o.value)}>
              {o.label}
            </option>
          ))}
        </select>
      )
      break
    }
    case 'password': {
      const saved = text === maskedSecret
      help = (
        <>
          {f.helpText}
          {f.helpText && saved && ' '}
          {saved && 'Saved. Type a new one to replace it.'}
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
    case 'path':
      control = <PathInput id={id} value={text} placeholder={f.placeholder ?? undefined} browse={browse} onChange={onChange} />
      break
    default:
      control = (
        <input
          id={id}
          className={ui.input}
          autoComplete="off"
          autoCapitalize="off"
          spellCheck={false}
          placeholder={f.placeholder ?? undefined}
          value={text}
          onChange={(e) => onChange(e.target.value)}
        />
      )
  }

  return (
    <Field id={f.type === 'checkbox' ? undefined : id} label={f.label} advanced={f.advanced} help={help} error={error}>
      {control}
    </Field>
  )
}

function PathInput({ id, value, placeholder, browse, onChange }: { id: string; value: string; placeholder?: string; browse: boolean; onChange: (value: string) => void }) {
  const [picking, setPicking] = useState(false)
  return (
    <div className={styles.pathRow}>
      <input id={id} className={`${ui.input} ${ui.mono}`} value={value} placeholder={placeholder} spellCheck={false} autoComplete="off" onChange={(e) => onChange(e.target.value)} />
      {browse && (
        <button className={ui.button} type="button" onClick={() => setPicking(true)}>
          Browse…
        </button>
      )}
      {picking && (
        <FolderPicker
          initialPath={value}
          onClose={() => setPicking(false)}
          onSelect={(selected) => {
            onChange(selected)
            setPicking(false)
          }}
        />
      )}
    </div>
  )
}
