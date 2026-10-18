import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useId, useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { ApiError } from '../api/client'
import type { SettingValue } from '../api/connect'
import {
  fieldApplies,
  importListApi,
  importListKeys,
  monitorDefault,
  type ImportList,
  type ImportListSchema,
  type ImportListType,
} from '../api/importLists'
import type { MediaType, RootFolder } from '../api/library'
import ProfileSelect from '../components/ProfileSelect'
import ui from '../components/ui.module.css'
import { Field, SettingField, Switch } from '../connect/ConnectionForm'
import Dialog from '../indexers/Dialog'
import form from '../indexers/Form.module.css'
import { AdvancedHint, AdvancedToggle } from '../lib/Advanced'
import { setAdvanced, useAdvanced } from '../lib/advancedMode'
import Preview from './Preview'
import { mediaLabel } from './labels'
import styles from './ImportLists.module.css'

/** Add or edit one import list: what it adds and how, the type's own fields, Test, Preview, Sync now, Save and Delete. */
export default function ListForm({
  list,
  type,
  schema,
  rootFolders,
  canEdit,
  onClose,
  onBack,
}: {
  list: ImportList
  type: ImportListType
  schema: ImportListSchema
  rootFolders: RootFolder[]
  canEdit: boolean
  onClose: () => void
  /** New lists: back to the type list. */
  onBack?: () => void
}) {
  const queryClient = useQueryClient()
  const formId = useId()
  const advanced = useAdvanced()
  const [draft, setDraft] = useState<ImportList>(list)
  const [edited, setEdited] = useState<Set<string>>(new Set())
  const [confirmDelete, setConfirmDelete] = useState(false)
  const isNew = list.id === 0

  const fields = type.fields.filter((f) => fieldApplies(f, draft))
  const showHidden = (error: Error) => {
    if (error instanceof ApiError && fields.some((f) => f.advanced && error.fieldErrors[f.name])) setAdvanced(true)
  }

  const done = async () => {
    await queryClient.invalidateQueries({ queryKey: importListKeys.all })
    onClose()
  }
  const save = useMutation({ mutationFn: () => (isNew ? importListApi.create(draft) : importListApi.update(draft)), onSuccess: done, onError: showHidden })
  const remove = useMutation({ mutationFn: () => importListApi.remove(list.id), onSuccess: done })
  const test = useMutation({ mutationFn: () => importListApi.test(draft), onError: showHidden })
  const preview = useMutation({ mutationFn: () => importListApi.preview(draft), onError: showHidden })
  const sync = useMutation({
    mutationFn: () => importListApi.sync(list.id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: importListKeys.list }),
  })

  const errorsOf = (e: Error | null) => (e instanceof ApiError ? e.fieldErrors : {})
  const fieldErrors = { ...errorsOf(preview.error), ...errorsOf(test.error), ...errorsOf(save.error) }
  const fieldError = (name: string) => (edited.has(name) ? undefined : fieldErrors[name]?.[0])
  const openErrors = Object.keys(fieldErrors).some((name) => !edited.has(name))
  const plainError = (e: Error | null) => (e && Object.keys(errorsOf(e)).length === 0 ? e.message : null)

  const resetResults = () => {
    setEdited(new Set())
    save.reset()
    test.reset()
    preview.reset()
    sync.reset()
  }
  const update = (change: Partial<ImportList>) => {
    setDraft((d) => ({ ...d, ...change }))
    setEdited((e) => new Set([...e, ...Object.keys(change)]))
  }
  const setting = (name: string, value: SettingValue) => {
    setDraft((d) => ({ ...d, settings: { ...d.settings, [name]: value } }))
    setEdited((e) => new Set([...e, name]))
  }
  const setMedia = (mediaType: MediaType) => {
    const root = rootFolders.find((r) => r.mediaType === mediaType)
    update({ mediaType, monitor: monitorDefault(mediaType), rootFolderId: root?.id ?? 0 })
  }

  const submit = (event: FormEvent) => {
    event.preventDefault()
    resetResults()
    save.mutate()
  }

  const id = (name: string) => `${formId}-${name}`
  const roots = rootFolders.filter((r) => r.mediaType === draft.mediaType)
  const monitorOptions = draft.mediaType === 'Series' ? schema.seriesMonitorOptions : schema.movieMonitorOptions
  const hiddenCount = fields.filter((f) => f.advanced).length
  const what = draft.mediaType === 'Series' ? 'series' : 'movies'

  const footer = canEdit ? (
    confirmDelete ? (
      <>
        <span className={form.confirm}>Delete {list.name}? What it added stays in the library.</span>
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
          {(test.isPending || preview.isPending) && <span className={ui.muted}>Reading the list…</span>}
          {test.isSuccess && <span className={ui.ok}>{test.data.message}</span>}
          {sync.isSuccess && <span className={ui.ok}>{sync.data.queued ? 'Sync started. The card shows the result.' : 'A sync is already waiting.'}</span>}
          {[plainError(test.error), plainError(preview.error), plainError(save.error), plainError(sync.error), plainError(remove.error)]
            .filter(Boolean)
            .map((message) => (
              <span key={message} className={ui.error}>
                {message}
              </span>
            ))}
          {openErrors && <span className={ui.error}>Check the highlighted fields.</span>}
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
        {!isNew && (
          <button
            className={ui.button}
            type="button"
            title="Read the saved list now and add what is new"
            onClick={() => {
              resetResults()
              sync.mutate()
            }}
            disabled={sync.isPending || !list.enabled}
          >
            Sync now
          </button>
        )}
        <button
          className={ui.button}
          type="button"
          onClick={() => {
            resetResults()
            preview.mutate()
          }}
          disabled={preview.isPending}
        >
          Preview
        </button>
        <button
          className={ui.button}
          type="button"
          onClick={() => {
            resetResults()
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
      title={isNew ? `Add ${type.name}` : canEdit ? `Edit ${list.name}` : list.name}
      subtitle={
        <>
          {type.description}
          {type.infoUrl && (
            <>
              {' '}
              <a href={type.infoUrl} target="_blank" rel="noreferrer">
                Open {new URL(type.infoUrl).hostname.replace(/^www\./, '')}
              </a>
            </>
          )}
        </>
      }
      size="wide"
      onClose={onClose}
      footer={footer}
      headerAction={<AdvancedToggle />}
    >
      <form id={formId} className={form.form} onSubmit={submit} noValidate>
        {!isNew && list.enabled && list.status.failing && <p className={form.statusNote}>Last sync failed: {list.status.lastError}</p>}
        <fieldset className={form.fieldset} disabled={!canEdit}>
          <Field id={id('name')} label="Name" help="Shown on the card, in the status window and the log." error={fieldError('name')}>
            <input id={id('name')} className={ui.input} value={draft.name} maxLength={100} onChange={(e) => update({ name: e.target.value })} autoFocus />
          </Field>

          <Field label="Enable" help="Off: the list is not read." error={fieldError('enabled')}>
            <Switch checked={draft.enabled} onChange={(enabled) => update({ enabled })} on="Enabled" off="Disabled" label="Enable" />
          </Field>

          {type.mediaTypes.length > 1 && (
            <Field label="Adds" help="One list adds movies or series. For both, add the list twice." error={fieldError('mediaType')}>
              <div className={styles.segmented} role="group" aria-label="Adds">
                {type.mediaTypes.map((m) => (
                  <button key={m} type="button" aria-pressed={draft.mediaType === m} onClick={() => setMedia(m)}>
                    {mediaLabel(m)}
                  </button>
                ))}
              </div>
            </Field>
          )}

          {fields
            .filter((f) => advanced || !f.advanced || fieldError(f.name))
            .map((f) => (
              <SettingField key={f.name} id={id(f.name)} field={f} value={draft.settings[f.name] ?? null} error={fieldError(f.name)} onChange={(v) => setting(f.name, v)} />
            ))}

          <Field label="Automatic add" help={`Add new ${what} at every sync. Off: open Preview and add them by hand.`} error={fieldError('enableAuto')}>
            <Switch checked={draft.enableAuto} onChange={(enableAuto) => update({ enableAuto })} label="Automatic add" />
          </Field>

          <Field id={id('monitor')} label="Monitor" help={draft.mediaType === 'Series' ? 'Which episodes are monitored. Specials never are.' : 'Movie and collection: the other movies of its collection are added too.'} error={fieldError('monitor')}>
            <select id={id('monitor')} className={ui.select} value={draft.monitor} onChange={(e) => update({ monitor: e.target.value })}>
              {monitorOptions.map((o) => (
                <option key={o.value} value={o.value}>
                  {o.label}
                </option>
              ))}
            </select>
          </Field>

          <Field
            id={id('root')}
            label="Root folder"
            error={fieldError('rootFolderId')}
            help={
              roots.length === 0 ? (
                <>
                  No {what} root folder yet. Add one under <Link to="/settings/media-management">Media management</Link>.
                </>
              ) : undefined
            }
          >
            <select id={id('root')} className={ui.select} value={draft.rootFolderId || ''} onChange={(e) => update({ rootFolderId: Number(e.target.value) })}>
              {roots.length === 0 && <option value="">None</option>}
              {roots.map((r) => (
                <option key={r.id} value={r.id}>
                  {r.path}
                </option>
              ))}
            </select>
          </Field>

          <Field label="Quality profile" error={fieldError('qualityProfileId')}>
            <ProfileSelect value={draft.qualityProfileId} onChange={(qualityProfileId) => update({ qualityProfileId })} disabled={!canEdit} />
          </Field>

          {draft.mediaType === 'Movie' && (
            <Field
              id={id('availability')}
              label="Minimum availability"
              help="Movies are added once they reach this. Grabbing follows the minimum availability under Settings → Indexers."
              error={fieldError('minimumAvailability')}
            >
              <select id={id('availability')} className={ui.select} value={draft.minimumAvailability} onChange={(e) => update({ minimumAvailability: e.target.value })}>
                {schema.availabilityOptions.map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </select>
            </Field>
          )}

          <Field label="Search on add" help={`Search for new ${what} right away. Off: RSS sync picks them up.`} error={fieldError('searchOnAdd')}>
            <Switch checked={draft.searchOnAdd} onChange={(searchOnAdd) => update({ searchOnAdd })} label="Search on add" />
          </Field>

        </fieldset>
        <AdvancedHint count={hiddenCount} />
        {preview.isSuccess && (
          <Preview key={preview.submittedAt} name={preview.data.name} items={preview.data.items} listId={isNew ? null : list.id} canEdit={canEdit} />
        )}
      </form>
    </Dialog>
  )
}
