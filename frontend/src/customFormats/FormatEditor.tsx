import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useId, useState, type FormEvent } from 'react'
import { ApiError } from '../api/client'
import {
  customFormatKeys,
  customFormatsApi,
  implementationLabel,
  type CustomFormat,
  type FormatSchema,
  type FormatSpecification,
  type SchemaField,
} from '../api/customFormats'
import { profileKeys } from '../api/profiles'
import Icon from '../components/Icon'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import { useAdvanced } from '../lib/advancedMode'
import { AdvancedToggle } from '../lib/Advanced'
import styles from './CustomFormats.module.css'
import { browserRegex, regexMatch } from './describe'

function emptyFields(schema: FormatSchema | undefined): FormatSpecification['fields'] {
  const fields: Record<string, string | number> = {}
  for (const f of schema?.fields ?? []) {
    if (f.type === 'select') fields[f.name] = f.options?.[0]?.value ?? 0
    else if (f.type === 'flags') fields[f.name] = 0
    else if (f.type === 'regex' || f.type === 'text') fields[f.name] = ''
  }
  return fields as FormatSpecification['fields']
}

/** Create or edit a custom format: name, media type, conditions with fields by type and a live regex check. */
export default function FormatEditor({
  format,
  canEdit,
  onClose,
  onDelete,
}: {
  format: CustomFormat
  canEdit: boolean
  onClose: () => void
  onDelete: () => void
}) {
  const queryClient = useQueryClient()
  const advanced = useAdvanced()
  const isNew = format.id === 0
  const [draft, setDraft] = useState<CustomFormat>(format)
  const [adding, setAdding] = useState(format.specifications.length === 0)
  const [sample, setSample] = useState('')
  const schema = useQuery({
    queryKey: customFormatKeys.schema(draft.mediaType),
    queryFn: () => customFormatsApi.schema(draft.mediaType),
    staleTime: Infinity,
  })

  const save = useMutation({
    mutationFn: () => {
      const body = {
        ...draft,
        name: draft.name.trim(),
        specifications: draft.specifications.map((s, i) => ({ ...s, name: s.name.trim() || `${implementationLabel(s.implementation)} ${i + 1}` })),
      }
      return isNew ? customFormatsApi.create(body) : customFormatsApi.update(body)
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: customFormatKeys.all })
      await queryClient.invalidateQueries({ queryKey: profileKeys.profiles })
      onClose()
    },
  })
  const fieldErrors = save.error instanceof ApiError ? save.error.fieldErrors : {}
  const specError = (index: number, field?: string) =>
    fieldErrors[field ? `specifications[${index}].fields.${field}` : `specifications[${index}]`]?.[0] ??
    (field ? undefined : fieldErrors[`specifications[${index}].name`]?.[0] ?? fieldErrors[`specifications[${index}].implementation`]?.[0])

  const setSpecs = (change: (specs: FormatSpecification[]) => FormatSpecification[]) =>
    setDraft((d) => ({ ...d, specifications: change(d.specifications) }))
  const updateSpec = (index: number, change: Partial<FormatSpecification>) =>
    setSpecs((specs) => specs.map((s, i) => (i === index ? { ...s, ...change } : s)))
  const moveSpec = (index: number, dir: -1 | 1) =>
    setSpecs((specs) => {
      const target = index + dir
      if (target < 0 || target >= specs.length) return specs
      const next = [...specs]
      ;[next[index], next[target]] = [next[target], next[index]]
      return next
    })
  const addSpec = (s: FormatSchema) => {
    setSpecs((specs) => [...specs, { name: '', implementation: s.implementation, negate: false, required: false, fields: emptyFields(s) }])
    setAdding(false)
  }

  const submit = (event: FormEvent) => {
    event.preventDefault()
    save.mutate()
  }

  const title = isNew ? 'New custom format' : canEdit ? `Edit ${format.name}` : format.name

  return (
    <Modal title={title} onClose={onClose}>
      <form className={styles.editor} onSubmit={submit}>
        <fieldset className={styles.fieldset} disabled={!canEdit}>
          <div className={styles.editorTop}>
            <label className={ui.field}>
              <span>Name</span>
              <input className={ui.input} value={draft.name} maxLength={100} onChange={(e) => setDraft({ ...draft, name: e.target.value })} autoFocus={isNew} />
              {fieldErrors.name && <small className={ui.error}>{fieldErrors.name[0]}</small>}
            </label>
            <div className={ui.field}>
              <span id="cf-type">For</span>
              {isNew ? (
                <div className={ui.seg} role="group" aria-labelledby="cf-type">
                  {(['movie', 'series'] as const).map((t) => (
                    <button
                      key={t}
                      type="button"
                      aria-pressed={draft.mediaType === t}
                      onClick={() => setDraft({ ...draft, mediaType: t, specifications: [] })}
                    >
                      <Icon name={t === 'movie' ? 'film' : 'tv'} size={14} />
                      {t === 'movie' ? 'Movies' : 'Series'}
                    </button>
                  ))}
                </div>
              ) : (
                <span className={styles.typeFixed}>
                  <Icon name={draft.mediaType === 'movie' ? 'film' : 'tv'} size={14} />
                  {draft.mediaType === 'movie' ? 'Movies' : 'Series'}
                </span>
              )}
            </div>
          </div>

          <label className={styles.switchRow}>
            <span className={ui.switch}>
              <input type="checkbox" role="switch" checked={draft.includeInRename} onChange={(e) => setDraft({ ...draft, includeInRename: e.target.checked })} />
              <span aria-hidden="true" />
            </span>
            <span>
              Include in file names
              <small>Shown by the {'{Custom Formats}'} token when a file is renamed.</small>
            </span>
          </label>

          {format.trashId && (
            <p className={ui.infoBox}>
              <Icon name="info" size={15} />
              From the TRaSH Guides. While auto update is on, the next sync replaces changes made here.
              {advanced && <span className={ui.mono}> {format.trashId}</span>}
            </p>
          )}

          <div className={styles.condHead}>
            <h3>
              Conditions <span className={styles.groupCount}>{draft.specifications.length}</span>
            </h3>
            <div className={styles.condTools}>
              <AdvancedToggle />
            </div>
          </div>
          <p className={styles.condHelp}>
            A format applies when every required condition matches and at least one condition of each type matches.
          </p>

          {draft.specifications.some((s) => schema.data?.find((x) => x.implementation === s.implementation)?.fields.some((f) => f.type === 'regex')) && (
            <label className={styles.sample}>
              <Icon name="sparkle" size={14} />
              <input
                className={`${ui.input} ${ui.mono}`}
                placeholder="Try a release name: the patterns below show if they match"
                aria-label="Release name to try the patterns on"
                value={sample}
                onChange={(e) => setSample(e.target.value)}
                spellCheck={false}
              />
            </label>
          )}

          <ol className={styles.conditions}>
            {draft.specifications.map((spec, index) => (
              <Condition
                key={index}
                spec={spec}
                index={index}
                count={draft.specifications.length}
                schemas={schema.data ?? []}
                sample={sample}
                advanced={advanced}
                error={specError(index)}
                fieldError={(field) => specError(index, field)}
                onChange={(change) => updateSpec(index, change)}
                onMove={(dir) => moveSpec(index, dir)}
                onRemove={() => setSpecs((specs) => specs.filter((_, i) => i !== index))}
              />
            ))}
          </ol>

          {canEdit &&
            (adding ? (
              <div className={styles.addPanel}>
                <div className={styles.addHead}>
                  <span>Add a condition</span>
                  {draft.specifications.length > 0 && (
                    <button type="button" className={`${ui.ghost} ${ui.sm}`} onClick={() => setAdding(false)}>
                      Cancel
                    </button>
                  )}
                </div>
                <div className={styles.addGrid}>
                  {(schema.data ?? []).map((s) => (
                    <button key={s.implementation} type="button" className={styles.addType} onClick={() => addSpec(s)}>
                      <strong>{s.label}</strong>
                      <small>{s.description}</small>
                    </button>
                  ))}
                </div>
                {schema.isError && <p className={ui.error}>{schema.error.message}</p>}
              </div>
            ) : (
              <button type="button" className={`${ui.button} ${styles.addButton}`} onClick={() => setAdding(true)}>
                <Icon name="plus" size={15} />
                Add condition
              </button>
            ))}
          {fieldErrors.specifications && <small className={ui.error}>{fieldErrors.specifications[0]}</small>}
        </fieldset>

        {save.error && Object.keys(fieldErrors).length === 0 && <p className={ui.errBox}>{save.error.message}</p>}
        {Object.keys(fieldErrors).length > 0 && !fieldErrors.name && <p className={ui.error}>Some conditions are not valid.</p>}

        {canEdit && (
          <div className={styles.dialogFoot}>
            {!isNew && (
              <button className={ui.ghost} type="button" onClick={onDelete}>
                <Icon name="trash" size={15} />
                Delete
              </button>
            )}
            <span className={styles.spacer} />
            <button className={ui.button} type="button" onClick={onClose}>
              Cancel
            </button>
            <button className={ui.primary} type="submit" disabled={save.isPending || !draft.name.trim()}>
              {save.isPending ? 'Saving…' : 'Save'}
            </button>
          </div>
        )}
      </form>
    </Modal>
  )
}

function Condition({
  spec,
  index,
  count,
  schemas,
  sample,
  advanced,
  error,
  fieldError,
  onChange,
  onMove,
  onRemove,
}: {
  spec: FormatSpecification
  index: number
  count: number
  schemas: FormatSchema[]
  sample: string
  advanced: boolean
  error?: string
  fieldError: (field: string) => string | undefined
  onChange: (change: Partial<FormatSpecification>) => void
  onMove: (dir: -1 | 1) => void
  onRemove: () => void
}) {
  const schema = schemas.find((s) => s.implementation === spec.implementation)
  const known = schemas.length === 0 || !!schema
  return (
    <li className={`${styles.condition} ${spec.negate ? styles.conditionNot : ''} ${error ? styles.conditionBad : ''}`}>
      <div className={styles.condRow}>
        <select
          className={`${ui.select} ${styles.implSelect}`}
          value={spec.implementation}
          aria-label={`Condition ${index + 1} type`}
          onChange={(e) => {
            const next = schemas.find((s) => s.implementation === e.target.value)
            onChange({ implementation: e.target.value, fields: emptyFields(next) })
          }}
        >
          {!known && <option value={spec.implementation}>{implementationLabel(spec.implementation)} (not supported)</option>}
          {schemas.map((s) => (
            <option key={s.implementation} value={s.implementation}>
              {s.label}
            </option>
          ))}
        </select>
        <input
          className={`${ui.input} ${styles.condName}`}
          value={spec.name}
          placeholder="Name"
          aria-label={`Condition ${index + 1} name`}
          onChange={(e) => onChange({ name: e.target.value })}
        />
        <span className={styles.condButtons}>
          {advanced && (
            <>
              <button type="button" className={`${ui.ghost} ${ui.sm} ${ui.icon}`} disabled={index === 0} onClick={() => onMove(-1)} aria-label={`Move condition ${index + 1} up`}>
                <Icon name="up" size={14} />
              </button>
              <button
                type="button"
                className={`${ui.ghost} ${ui.sm} ${ui.icon}`}
                disabled={index === count - 1}
                onClick={() => onMove(1)}
                aria-label={`Move condition ${index + 1} down`}
              >
                <Icon name="down" size={14} />
              </button>
            </>
          )}
          <button type="button" className={`${ui.ghost} ${ui.sm} ${ui.icon}`} onClick={onRemove} aria-label={`Remove condition ${index + 1}`}>
            <Icon name="x" size={14} />
          </button>
        </span>
      </div>

      <div className={styles.condFields}>
        {(schema?.fields ?? []).map((field) => (
          <FieldInput
            key={field.name}
            field={field}
            single={schema?.fields.length === 1}
            label={`Condition ${index + 1} ${field.label.toLowerCase()}`}
            value={spec.fields[field.name]}
            sample={sample}
            error={fieldError(field.name)}
            onChange={(value) => onChange({ fields: { ...spec.fields, [field.name]: value } })}
          />
        ))}
      </div>

      <div className={styles.condSwitches}>
        <label className={styles.miniSwitch} title="The condition counts when it does NOT match.">
          <span className={ui.switch}>
            <input type="checkbox" role="switch" checked={spec.negate} onChange={(e) => onChange({ negate: e.target.checked })} />
            <span aria-hidden="true" />
          </span>
          <span>
            Negate <small>counts when it does not match</small>
          </span>
        </label>
        <label className={styles.miniSwitch} title="Must match. Without it, one matching condition of the same type is enough.">
          <span className={ui.switch}>
            <input type="checkbox" role="switch" checked={spec.required} onChange={(e) => onChange({ required: e.target.checked })} />
            <span aria-hidden="true" />
          </span>
          <span>
            Required <small>must match, not just one of its type</small>
          </span>
        </label>
      </div>
      {error && <small className={ui.error}>{error}</small>}
    </li>
  )
}

function FieldInput({
  field,
  single,
  label,
  value,
  sample,
  error,
  onChange,
}: {
  field: SchemaField
  /** The only field of its condition: no label above it (the type says it all). */
  single: boolean
  label: string
  value: string | number | undefined
  sample: string
  error?: string
  onChange: (value: string | number | undefined) => void
}) {
  const id = useId()
  const caption = single ? null : <span>{field.label}</span>
  if (field.type === 'select') {
    return (
      <label className={`${ui.field} ${styles.fieldSelect}`}>
        {caption}
        <select className={ui.select} value={value ?? ''} onChange={(e) => onChange(Number(e.target.value))} aria-label={single ? label : undefined}>
          {(field.options ?? []).map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
        {error && <small className={ui.error}>{error}</small>}
      </label>
    )
  }
  if (field.type === 'flags') {
    const bits = Number(value ?? 0)
    return (
      <fieldset className={styles.flags}>
        <legend className={single ? styles.srOnly : undefined}>{field.label}</legend>
        {(field.options ?? []).map((o) => (
          <label key={o.value} className={ui.check}>
            <input type="checkbox" checked={(bits & o.value) === o.value && o.value !== 0} onChange={(e) => onChange(e.target.checked ? bits | o.value : bits & ~o.value)} />
            <span>{o.label}</span>
          </label>
        ))}
        {error && <small className={ui.error}>{error}</small>}
      </fieldset>
    )
  }
  if (field.type === 'number') {
    return (
      <label className={`${ui.field} ${styles.fieldNumber}`} htmlFor={id}>
        <span>{field.label}</span>
        <span className={styles.withUnit}>
          <input
            id={id}
            className={`${ui.input} ${ui.num}`}
            type="number"
            step="any"
            inputMode="decimal"
            value={value ?? ''}
            onChange={(e) => onChange(e.target.value === '' ? undefined : e.target.valueAsNumber)}
          />
          {field.unit && <span className={styles.unit}>{field.unit}</span>}
        </span>
        {error && <small className={ui.error}>{error}</small>}
      </label>
    )
  }
  // text and regex
  const text = String(value ?? '')
  const isRegex = field.type === 'regex'
  const valid = !isRegex || text === '' || browserRegex(text) !== null
  const match = isRegex && sample && text && valid ? regexMatch(text, sample) : null
  return (
    <label className={`${ui.field} ${styles.fieldText}`}>
      {caption}
      <input
        className={`${ui.input} ${isRegex ? ui.mono : ''} ${!valid || error ? styles.inputBad : ''}`}
        aria-label={single ? label : undefined}
        value={text}
        spellCheck={false}
        placeholder={isRegex ? '\\b(DL)\\b' : ''}
        onChange={(e) => onChange(e.target.value)}
      />
      {error ? (
        <small className={ui.error}>{error}</small>
      ) : !valid ? (
        <small className={styles.regexWarn}>The browser cannot read this pattern; the server checks it when you save.</small>
      ) : isRegex && sample && text ? (
        match ? (
          <small className={styles.regexHit}>
            <Icon name="check" size={12} />
            Matches{' '}
            <span className={styles.sampleText}>
              {sample.slice(Math.max(0, match.index - 12), match.index)}
              <mark>{sample.slice(match.index, match.index + match.length) || '∅'}</mark>
              {sample.slice(match.index + match.length, match.index + match.length + 12)}
            </span>
          </small>
        ) : (
          <small className={styles.regexMiss}>
            <Icon name="x" size={12} />
            No match in the sample
          </small>
        )
      ) : null}
    </label>
  )
}
