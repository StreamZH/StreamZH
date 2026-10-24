import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useId, useState, type FormEvent, type ReactNode } from 'react'
import { ApiError } from '../api/client'
import {
  genericDefinitionKey,
  indexerKeys,
  indexersApi,
  isGenericDefinition,
  maskedApiKey,
  protocolOf,
  standardCategoryNames,
  type Indexer,
  type IndexerCapabilities,
  type IndexerCategory,
  type IndexerDefinition,
} from '../api/indexers'
import ui from '../components/ui.module.css'
import { AdvancedHint, AdvancedToggle } from '../lib/Advanced'
import { advancedLabel, useAdvanced } from '../lib/advancedMode'
import { PrivacyChip, ProtocolChip } from './Chips'
import Dialog from './Dialog'
import { formatUntil, hostOf, throughProwlarr } from './model'
import styles from './Form.module.css'

/** Top-level categories in a Newznab range (2000 movies, 5000 TV) with their sub-categories. */
function categoryTree(caps: IndexerCapabilities | null, base: number): IndexerCategory[] {
  const inRange = (id: number) => id >= base && id < base + 1000
  if (caps) return caps.categories.filter((c) => inRange(c.id))
  const ids = Object.keys(standardCategoryNames)
    .map(Number)
    .filter((id) => inRange(id) && id !== base)
  return [{ id: base, name: standardCategoryNames[base], subCategories: ids.map((id) => ({ id, name: standardCategoryNames[id], subCategories: [] })) }]
}

function Field({
  id,
  label,
  help,
  error,
  advanced = false,
  children,
}: {
  id?: string
  label: string
  help?: ReactNode
  error?: string
  /** Expert setting: only shown in advanced mode (or while it has an error, so nobody is stuck). */
  advanced?: boolean
  children: ReactNode
}) {
  const advancedMode = useAdvanced()
  if (advanced && !advancedMode && !error) return null
  const labelClass = `${styles.label} ${advanced ? advancedLabel : ''}`
  return (
    <div className={`${styles.row} ${error ? styles.invalid : ''}`}>
      {id ? (
        <label className={labelClass} htmlFor={id}>
          {label}
        </label>
      ) : (
        <span className={labelClass}>{label}</span>
      )}
      <div className={styles.control}>
        {children}
        {help && <small className={styles.help}>{help}</small>}
        {error && <small className={styles.fieldError}>{error}</small>}
      </div>
    </div>
  )
}

/** Number input where empty means "unlimited" (null). */
function limitValue(value: number | null) {
  return value == null || Number.isNaN(value) ? '' : value
}

/**
 * Add or edit a Newznab indexer, single column like Prowlarr's "Edit Indexer" form.
 * `definition` is the catalog entry (preset) the indexer comes from, if known.
 */
export default function IndexerForm({
  indexer,
  definition,
  capabilities = null,
  canEdit,
  onClose,
  onBack,
  movedFromProwlarr = false,
}: {
  indexer: Indexer
  definition: IndexerDefinition | null
  capabilities?: IndexerCapabilities | null
  canEdit: boolean
  onClose: () => void
  /** Shown as "Back" for a new indexer picked from the catalog. */
  onBack?: () => void
  /** The draft was just moved from Prowlarr's proxy to the indexer's own address: entering the key switches it on. */
  movedFromProwlarr?: boolean
}) {
  const queryClient = useQueryClient()
  const formId = useId()
  const [draft, setDraft] = useState<Indexer>(indexer)
  const [caps, setCaps] = useState<IndexerCapabilities | null>(capabilities)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const isNew = indexer.id === 0

  // Saved indexers only know their definition key; look up the catalog entry for the preset hints.
  const definitions = useQuery({
    queryKey: indexerKeys.definitions,
    queryFn: indexersApi.definitions,
    staleTime: 10 * 60_000,
    enabled: !definition && !!indexer.definitionKey,
  })
  const preset = definition ?? definitions.data?.find((d) => d.key === indexer.definitionKey) ?? null
  // A preset with a fixed address (not Generic Newznab).
  const fixed = preset && !isGenericDefinition(preset.key) && preset.baseUrl !== '' ? preset : null
  const torrent = (preset?.protocol ?? protocolOf(draft)) === 'torrent'
  // A saved address other than the preset's (moved indexer, or Prowlarr's proxy) is shown without the Advanced switch.
  const addressDiffers = fixed != null && (indexer.baseUrl !== fixed.baseUrl || (indexer.apiPath || '/api') !== (fixed.apiPath || '/api'))
  const viaProwlarr = throughProwlarr(draft)
  // Back to the preset's address and API path. A key saved for another address (Prowlarr's) is not sent there.
  const resetAddress = () =>
    fixed &&
    update({
      baseUrl: fixed.baseUrl,
      apiPath: fixed.apiPath || '/api',
      ...(draft.apiKey === maskedApiKey && indexer.baseUrl !== fixed.baseUrl ? { apiKey: '' } : {}),
    })

  const done = async () => {
    await queryClient.invalidateQueries({ queryKey: indexerKeys.all })
    onClose()
  }

  const save = useMutation({
    mutationFn: () => (isNew ? indexersApi.create(draft) : indexersApi.update(draft)),
    onSuccess: done,
  })
  const remove = useMutation({ mutationFn: () => indexersApi.remove(indexer.id), onSuccess: done })
  const test = useMutation({
    mutationFn: () => indexersApi.test(draft),
    onSuccess: (data) => setCaps(data),
  })

  // Fields changed since the last save or test: their old error no longer applies and is hidden.
  const [edited, setEdited] = useState<Set<string>>(new Set())
  const update = (change: Partial<Indexer>) => {
    setDraft((d) => ({ ...d, ...change }))
    setEdited((e) => {
      const next = new Set(e)
      for (const key of Object.keys(change)) next.add(key === 'tvCategories' ? 'categories' : key)
      return next
    })
  }

  const saveErrors = save.error instanceof ApiError ? save.error.fieldErrors : {}
  const testErrors = test.error instanceof ApiError ? test.error.fieldErrors : {}
  const fieldError = (name: string) => (edited.has(name) ? undefined : (saveErrors[name] ?? testErrors[name])?.[0])
  const openErrors = [...Object.keys(saveErrors), ...Object.keys(testErrors)].some((name) => !edited.has(name))

  const submit = (event: FormEvent) => {
    event.preventDefault()
    setEdited(new Set())
    save.mutate()
  }

  const id = (name: string) => `${formId}-${name}`
  const unit = draft.limitsUnit === 'hour' ? 'hour' : 'day'
  const status = indexer.status
  const title = isNew
    ? `Add ${preset && !isGenericDefinition(preset.key) ? preset.name : torrent ? 'Torznab indexer' : 'Newznab indexer'}`
    : canEdit
      ? `Edit ${indexer.name}`
      : indexer.name

  const subtitle = (
    <>
      {preset ? (
        <>
          <ProtocolChip protocol={preset.protocol} />
          <PrivacyChip privacy={preset.privacy} />
          <span>{preset.key === genericDefinitionKey ? 'Generic Newznab' : isGenericDefinition(preset.key) ? 'Generic Torznab' : preset.description}</span>
        </>
      ) : (
        <>
          <ProtocolChip protocol={torrent ? 'torrent' : 'usenet'} />
          <span>{torrent ? 'Generic Torznab' : 'Generic Newznab'}</span>
        </>
      )}
    </>
  )

  const footer = canEdit ? (
    confirmDelete ? (
      <>
        <span className={styles.confirm}>Delete {indexer.name}?</span>
        <span className={styles.spacer} />
        <button className={ui.button} type="button" onClick={() => setConfirmDelete(false)}>
          Keep
        </button>
        <button className={ui.dangerSolid} type="button" onClick={() => remove.mutate()} disabled={remove.isPending}>
          {remove.isPending ? 'Deleting…' : 'Yes, delete'}
        </button>
      </>
    ) : (
      <>
        <div className={styles.testResult} aria-live="polite">
          {test.isPending && <span className={ui.muted}>Testing the connection…</span>}
          {test.isSuccess && (
            <span className={ui.ok}>
              Connection works{test.data.categories.length > 0 ? `, ${test.data.categories.length} category groups loaded` : ''}.
            </span>
          )}
          {test.error && Object.keys(testErrors).length === 0 && <span className={ui.error}>{test.error.message}</span>}
          {save.error && Object.keys(saveErrors).length === 0 && <span className={ui.error}>{save.error.message}</span>}
          {openErrors && <span className={ui.error}>Check the highlighted fields.</span>}
          {remove.error && <span className={ui.error}>{remove.error.message}</span>}
        </div>
        {!isNew && (
          <button className={ui.danger} type="button" onClick={() => setConfirmDelete(true)}>
            Delete
          </button>
        )}
        {isNew && onBack && (
          <button className={ui.button} type="button" onClick={onBack}>
            ← Back
          </button>
        )}
        <span className={styles.spacer} />
        <button className={ui.button} type="button" onClick={() => {
            setEdited(new Set())
            test.mutate()
          }} disabled={test.isPending}>
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
      <span className={styles.spacer} />
      <button className={ui.button} type="button" onClick={onClose}>
        Close
      </button>
    </>
  )

  return (
    <Dialog title={title} subtitle={subtitle} onClose={onClose} footer={footer} headerAction={<AdvancedToggle />}>
      <form id={formId} className={styles.form} onSubmit={submit} noValidate>
        {status && !status.healthy && status.disabledTill && (
          <p className={styles.statusNote}>
            Skipped until {formatUntil(status.disabledTill)}
            {status.lastError ? `: ${status.lastError}` : ''}
          </p>
        )}
        {movedFromProwlarr && !viaProwlarr && fixed && (
          <p className={styles.infoNote} role="note">
            Now at {hostOf(fixed.baseUrl)} instead of Prowlarr. Enter your API key from {hostOf(fixed.siteUrl ?? fixed.baseUrl)}, press Test, then Save.
            {!indexer.enable && ' The indexer is switched on with the key.'}
          </p>
        )}
        {viaProwlarr && (
          <div className={styles.warnNote} role="note">
            <span>
              This URL is Prowlarr&apos;s, not the indexer&apos;s own address. StreamZH talks to indexers directly:{' '}
              {fixed ? `use ${hostOf(fixed.baseUrl)} with your API key from the indexer.` : 'enter the indexer’s own URL and API key.'}
            </span>
            {canEdit && fixed && (
              <button className={`${ui.button} ${ui.sm}`} type="button" onClick={resetAddress}>
                Use {hostOf(fixed.baseUrl)}
              </button>
            )}
          </div>
        )}
        <fieldset className={styles.fieldset} disabled={!canEdit}>
          <Field id={id('name')} label="Name" help="Shown in search results and history." error={fieldError('name')}>
            <input
              id={id('name')}
              className={ui.input}
              value={draft.name}
              maxLength={100}
              placeholder={fixed ? undefined : 'My indexer'}
              onChange={(e) => update({ name: e.target.value })}
              autoFocus
            />
          </Field>

          <Field label="Enable" help="Off: StreamZH never uses this indexer, not even for interactive search." error={fieldError('enable')}>
            <label className={styles.switch}>
              <input type="checkbox" role="switch" checked={draft.enable} onChange={(e) => update({ enable: e.target.checked })} />
              <span className={styles.track} aria-hidden="true" />
              <span>{draft.enable ? 'Enabled' : 'Disabled'}</span>
            </label>
          </Field>

          <Field advanced={fixed != null && !addressDiffers}
            id={id('baseUrl')}
            label="URL"
            error={fieldError('baseUrl')}
            help={
              fixed ? (
                <>
                  Preset for {fixed.name}. Change it only if the indexer moved.
                  {(draft.baseUrl !== fixed.baseUrl || (draft.apiPath || '/api') !== (fixed.apiPath || '/api')) && (
                    <>
                      {' '}
                      <button type="button" className={styles.inlineLink} onClick={resetAddress}>
                        Reset to {hostOf(fixed.baseUrl)}
                      </button>
                    </>
                  )}
                </>
              ) : torrent ? (
                <>
                  The Torznab feed. From Jackett: <code>http://host:9117/api/v2.0/indexers/name/results/torznab/</code>; from Prowlarr:{' '}
                  <code>http://host:9696/1/</code>.
                </>
              ) : (
                <>The indexer&apos;s own API address, e.g. <code>https://api.indexer.example</code>, not Prowlarr&apos;s.</>
              )
            }
          >
            <input
              id={id('baseUrl')}
              className={`${ui.input} ${fixed && draft.baseUrl === fixed.baseUrl ? styles.presetValue : ''}`}
              type="url"
              inputMode="url"
              autoComplete="off"
              spellCheck={false}
              placeholder="https://indexer.example"
              value={draft.baseUrl}
              onChange={(e) => update({ baseUrl: e.target.value })}
            />
          </Field>

          <Field advanced={!addressDiffers}
            id={id('apiPath')}
            label="API path"
            error={fieldError('apiPath')}
            help={fixed ? `Preset ${fixed.apiPath || '/api'}.` : `Path to the ${torrent ? 'Torznab' : 'Newznab'} API, usually /api.`}
          >
            <input
              id={id('apiPath')}
              className={`${ui.input} ${ui.mono}`}
              spellCheck={false}
              placeholder="/api"
              value={draft.apiPath}
              onChange={(e) => update({ apiPath: e.target.value })}
            />
          </Field>

          <Field
            id={id('apiKey')}
            label="API key"
            error={fieldError('apiKey')}
            help={
              draft.apiKey === maskedApiKey ? (
                'Saved. Type a new key to replace it.'
              ) : preset?.siteUrl ? (
                <>
                  From your account or profile page at{' '}
                  <a href={preset.siteUrl} target="_blank" rel="noreferrer noopener">
                    {hostOf(preset.siteUrl)}
                  </a>
                  .
                </>
              ) : (
                "From your account or profile page on the indexer's website."
              )
            }
          >
            <input
              id={id('apiKey')}
              className={`${ui.input} ${ui.mono}`}
              type="password"
              autoComplete="off"
              spellCheck={false}
              value={draft.apiKey}
              onFocus={(e) => draft.apiKey === maskedApiKey && e.target.select()}
              onChange={(e) =>
                // Moved from Prowlarr: the key is all that was missing, so the indexer is switched on with it.
                update(movedFromProwlarr && !indexer.enable && e.target.value.trim() !== '' ? { apiKey: e.target.value, enable: true } : { apiKey: e.target.value })
              }
            />
          </Field>

          <Field advanced
            id={id('additionalParameters')}
            label="Additional parameters"
            error={fieldError('additionalParameters')}
            help={
              <>
                Added to every request, e.g. <code>&amp;attrs=poster</code> or <code>dl=1</code>.
              </>
            }
          >
            <input
              id={id('additionalParameters')}
              className={`${ui.input} ${ui.mono}`}
              spellCheck={false}
              autoComplete="off"
              value={draft.additionalParameters}
              onChange={(e) => update({ additionalParameters: e.target.value })}
            />
          </Field>

          <Field advanced
            id={id('vipExpiration')}
            label="VIP expiration"
            error={fieldError('vipExpiration')}
            help="When your paid membership ends. StreamZH warns you 7 days before."
          >
            <div className={styles.inline}>
              <input
                id={id('vipExpiration')}
                className={`${ui.input} ${styles.date}`}
                type="date"
                value={draft.vipExpiration ?? ''}
                onChange={(e) => update({ vipExpiration: e.target.value || null })}
              />
              {draft.vipExpiration && (
                <button type="button" className={styles.inlineLink} onClick={() => update({ vipExpiration: null })}>
                  Clear
                </button>
              )}
            </div>
          </Field>

          <Field advanced
            id={id('queryLimit')}
            label="Query limit"
            error={fieldError('queryLimit')}
            help={`Most API queries per ${unit}. Empty for unlimited. StreamZH stops querying once it is reached.`}
          >
            <input
              id={id('queryLimit')}
              className={`${ui.input} ${styles.number}`}
              type="number"
              min={0}
              inputMode="numeric"
              placeholder="Unlimited"
              value={limitValue(draft.queryLimit)}
              onChange={(e) => update({ queryLimit: e.target.value === '' ? null : e.target.valueAsNumber })}
            />
          </Field>

          <Field advanced
            id={id('grabLimit')}
            label="Grab limit"
            error={fieldError('grabLimit')}
            help={`Most ${torrent ? '.torrent' : 'NZB'} downloads per ${unit}. Empty for unlimited.`}
          >
            <input
              id={id('grabLimit')}
              className={`${ui.input} ${styles.number}`}
              type="number"
              min={0}
              inputMode="numeric"
              placeholder="Unlimited"
              value={limitValue(draft.grabLimit)}
              onChange={(e) => update({ grabLimit: e.target.value === '' ? null : e.target.valueAsNumber })}
            />
          </Field>

          <Field advanced id={id('limitsUnit')} label="Limits unit" error={fieldError('limitsUnit')} help="Limits count over the last 24 hours or the last hour.">
            <select
              id={id('limitsUnit')}
              className={`${ui.select} ${styles.number}`}
              value={draft.limitsUnit}
              onChange={(e) => update({ limitsUnit: e.target.value === 'hour' ? 'hour' : 'day' })}
            >
              <option value="day">Day</option>
              <option value="hour">Hour</option>
            </select>
          </Field>

          <Field advanced
            id={id('priority')}
            label="Indexer priority"
            error={fieldError('priority')}
            help="1 to 50, 1 is the highest. Breaks ties between equally good releases."
          >
            <input
              id={id('priority')}
              className={`${ui.input} ${styles.number}`}
              type="number"
              min={1}
              max={50}
              inputMode="numeric"
              value={Number.isNaN(draft.priority) ? '' : draft.priority}
              onChange={(e) => update({ priority: e.target.valueAsNumber })}
            />
          </Field>

          {torrent && (
            <>
              <Field
                id={id('minimumSeeders')}
                label="Minimum seeders"
                error={fieldError('minimumSeeders')}
                help="Releases with fewer seeders are rejected: they would hardly download."
              >
                <input
                  id={id('minimumSeeders')}
                  className={`${ui.input} ${styles.number}`}
                  type="number"
                  min={0}
                  inputMode="numeric"
                  value={limitValue(draft.minimumSeeders)}
                  onChange={(e) => update({ minimumSeeders: e.target.value === '' ? null : e.target.valueAsNumber })}
                />
              </Field>

              <Field id={id('seedRatio')} label="Seed ratio" error={fieldError('seedRatio')} help="Seed until this ratio after the download. Empty: the download client's default.">
                <input
                  id={id('seedRatio')}
                  className={`${ui.input} ${styles.number}`}
                  type="number"
                  min={0}
                  step={0.1}
                  inputMode="decimal"
                  placeholder="Client default"
                  value={limitValue(draft.seedRatio)}
                  onChange={(e) => update({ seedRatio: e.target.value === '' ? null : e.target.valueAsNumber })}
                />
              </Field>

              <Field
                id={id('seedTimeMinutes')}
                label="Seed time"
                error={fieldError('seedTimeMinutes')}
                help="Seed at least this long. Private trackers often ask for 72 hours or more. Empty: the client's default."
              >
                <MinutesInput id={id('seedTimeMinutes')} value={draft.seedTimeMinutes} onChange={(seedTimeMinutes) => update({ seedTimeMinutes })} />
              </Field>

              <Field
                advanced
                id={id('seasonPackSeedTimeMinutes')}
                label="Season-pack seed time"
                error={fieldError('seasonPackSeedTimeMinutes')}
                help="Seed time for whole seasons. Empty: the seed time above."
              >
                <MinutesInput
                  id={id('seasonPackSeedTimeMinutes')}
                  value={draft.seasonPackSeedTimeMinutes}
                  onChange={(seasonPackSeedTimeMinutes) => update({ seasonPackSeedTimeMinutes })}
                />
              </Field>
            </>
          )}

          <Field label="Use for" help="RSS watches for new releases, automatic search runs on its own, interactive search is the Search button.">
            <div className={styles.checks}>
              <label className={ui.check}>
                <input type="checkbox" checked={draft.enableRss} onChange={(e) => update({ enableRss: e.target.checked })} />
                <span>RSS</span>
              </label>
              <label className={ui.check}>
                <input type="checkbox" checked={draft.enableAutomaticSearch} onChange={(e) => update({ enableAutomaticSearch: e.target.checked })} />
                <span>Automatic search</span>
              </label>
              <label className={ui.check}>
                <input
                  type="checkbox"
                  checked={draft.enableInteractiveSearch}
                  onChange={(e) => update({ enableInteractiveSearch: e.target.checked })}
                />
                <span>Interactive search</span>
              </label>
            </div>
          </Field>

          <Field advanced
            label="Categories"
            error={fieldError('categories')}
            help={caps ? undefined : 'Standard Newznab categories. Test to load the indexer’s own list.'}
          >
            <div className={styles.pickers}>
              <CategoryPicker
                legend="Movies"
                tree={categoryTree(caps, 2000)}
                selected={draft.categories}
                onChange={(categories) => update({ categories })}
              />
              <CategoryPicker
                legend="TV"
                tree={categoryTree(caps, 5000)}
                selected={draft.tvCategories}
                onChange={(tvCategories) => update({ tvCategories })}
              />
            </div>
          </Field>
        </fieldset>
        <AdvancedHint count={(fixed ? 9 : 8) + (torrent ? 1 : 0)} />
      </form>
    </Dialog>
  )
}

/** Minutes, typed as hours or days with a unit select ("72 hours" is easier than "4320"). Empty = null. */
function MinutesInput({ id, value, onChange }: { id: string; value: number | null; onChange: (minutes: number | null) => void }) {
  const initialUnit = value != null && value > 0 && value % 1440 === 0 ? 1440 : value != null && value > 0 && value % 60 !== 0 ? 1 : 60
  const [unit, setUnit] = useState(initialUnit)
  const shown = value == null || Number.isNaN(value) ? '' : Math.round((value / unit) * 100) / 100
  return (
    <div className={styles.inline}>
      <input
        id={id}
        className={`${ui.input} ${styles.number}`}
        type="number"
        min={0}
        step={unit === 1 ? 1 : 0.5}
        inputMode="decimal"
        placeholder="Client default"
        value={shown}
        onChange={(e) => onChange(e.target.value === '' ? null : Math.round(e.target.valueAsNumber * unit))}
      />
      <select className={`${ui.select} ${styles.unitSelect}`} aria-label="Unit" value={unit} onChange={(e) => setUnit(Number(e.target.value))}>
        <option value={1}>minutes</option>
        <option value={60}>hours</option>
        <option value={1440}>days</option>
      </select>
    </div>
  )
}

function CategoryPicker({
  legend,
  tree,
  selected,
  onChange,
}: {
  legend: string
  tree: IndexerCategory[]
  selected: number[]
  onChange: (ids: number[]) => void
}) {
  const shown = new Set<number>()
  tree.forEach((c) => {
    shown.add(c.id)
    c.subCategories.forEach((s) => shown.add(s.id))
  })
  // Selected ids the list does not know (e.g. saved before the indexer changed its categories).
  const extra = selected.filter((id) => !shown.has(id))

  const toggle = (id: number, checked: boolean) =>
    onChange(checked ? [...selected, id].sort((a, b) => a - b) : selected.filter((c) => c !== id))

  const box = (id: number, name: string) => (
    <label className={ui.check} key={id}>
      <input type="checkbox" checked={selected.includes(id)} onChange={(e) => toggle(id, e.target.checked)} />
      <span>
        {name} <span className={styles.catId}>{id}</span>
      </span>
    </label>
  )

  return (
    <fieldset className={styles.categories}>
      <legend>
        {legend} <span className={styles.catCount}>{selected.length} selected</span>
      </legend>
      {tree.length === 0 && extra.length === 0 && <p className={ui.muted}>The indexer has no categories in this range.</p>}
      {tree.map((parent) => (
        <div key={parent.id} className={styles.categoryGroup}>
          {box(parent.id, parent.name)}
          {parent.subCategories.length > 0 && <div className={styles.subCategories}>{parent.subCategories.map((s) => box(s.id, s.name))}</div>}
        </div>
      ))}
      {extra.length > 0 && <div className={styles.subCategories}>{extra.map((id) => box(id, standardCategoryNames[id] ?? 'Category'))}</div>}
    </fieldset>
  )
}
