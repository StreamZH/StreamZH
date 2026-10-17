import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { ApiError } from '../api/client'
import {
  indexerKeys,
  indexersApi,
  maskedApiKey,
  standardCategoryNames,
  type Indexer,
  type IndexerCapabilities,
  type IndexerCategory,
  type IndexerStatus,
} from '../api/indexers'
import { useAuth } from '../auth/auth'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import page from './Page.module.css'
import styles from './Indexers.module.css'

export default function Indexers() {
  const { user } = useAuth()
  const canEdit = user?.role === 'Owner' || user?.role === 'Admin'

  return (
    <div className={page.page}>
      <header>
        <h1 className={page.title}>Indexers</h1>
        <p className={page.lead}>Where StreamZH searches for releases. Failing indexers are skipped for a while and tried again later.</p>
      </header>
      <IndexerList canEdit={canEdit} />
    </div>
  )
}

function hostOf(url: string) {
  try {
    return new URL(url).host
  } catch {
    return url
  }
}

function formatUntil(value: string) {
  const date = new Date(value)
  const time = date.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' })
  if (date.toDateString() === new Date().toDateString()) return time
  return `${date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })}, ${time}`
}

function HealthBadge({ status }: { status: IndexerStatus | null }) {
  if (status && !status.healthy && status.disabledTill) {
    return <span className={`${ui.badge} ${ui.missing}`}>Disabled until {formatUntil(status.disabledTill)}</span>
  }
  return <span className={`${ui.badge} ${ui.ok}`}>OK</span>
}

function uses(i: Indexer) {
  const list = [i.enableRss && 'RSS', i.enableAutomaticSearch && 'Automatic', i.enableInteractiveSearch && 'Interactive'].filter(Boolean)
  return list.length > 0 ? list.join(' · ') : 'Off'
}

function IndexerList({ canEdit }: { canEdit: boolean }) {
  const indexers = useQuery({ queryKey: indexerKeys.list, queryFn: indexersApi.list, refetchInterval: 60_000 })
  const [editing, setEditing] = useState<Indexer | null>(null)

  const create = useMutation({
    mutationFn: indexersApi.schema,
    onSuccess: (schema) => setEditing(schema),
  })

  const list = indexers.data ?? []
  const addButton = canEdit && (
    <button className={ui.primary} type="button" onClick={() => create.mutate()} disabled={create.isPending}>
      Add indexer
    </button>
  )

  return (
    <section className={page.panel}>
      <div className={styles.panelHead}>
        <h2 className={page.panelTitle}>Newznab indexers</h2>
        {list.length > 0 && addButton}
      </div>
      {indexers.isError && <p className={ui.error}>{indexers.error.message}</p>}
      {create.error && <p className={ui.error}>{create.error.message}</p>}

      {indexers.isSuccess && list.length === 0 && (
        <div className={styles.empty}>
          <p>
            StreamZH searches Usenet indexers through <strong>Newznab</strong>, the API most Usenet indexers offer. You need the indexer&apos;s
            address and the API key from your account page there.
          </p>
          <p className={styles.hint}>
            Using Prowlarr? Add each indexer with Prowlarr&apos;s Newznab URL (<code>http://host:9696/1/</code>) and Prowlarr&apos;s API key.
          </p>
          {canEdit ? addButton : <p className={ui.muted}>An admin can add indexers.</p>}
        </div>
      )}

      {list.length > 0 && (
        <ul className={styles.list}>
          {list.map((i) => (
            <li key={i.id} className={styles.row}>
              <div className={styles.rowMain}>
                <span className={styles.name}>{i.name}</span>
                <HealthBadge status={i.status} />
              </div>
              <dl className={styles.meta}>
                <div>
                  <dt>Host</dt>
                  <dd className={ui.mono}>{hostOf(i.baseUrl)}</dd>
                </div>
                <div>
                  <dt>Priority</dt>
                  <dd>{i.priority}</dd>
                </div>
                <div>
                  <dt>Used for</dt>
                  <dd>{uses(i)}</dd>
                </div>
              </dl>
              <div className={styles.rowActions}>
                <button className={ui.button} type="button" onClick={() => setEditing(i)}>
                  {canEdit ? 'Edit' : 'View'}
                </button>
              </div>
              {i.status?.lastError && (
                <p className={styles.lastError}>
                  {i.status.healthy ? 'Last error' : 'Error'}: {i.status.lastError}
                </p>
              )}
            </li>
          ))}
        </ul>
      )}

      {editing && <IndexerDialog indexer={editing} canEdit={canEdit} onClose={() => setEditing(null)} />}
    </section>
  )
}

/** Top-level categories in a Newznab range (2000 movies, 5000 TV) with their sub-categories. */
function categoryTree(caps: IndexerCapabilities | null, base: number): IndexerCategory[] {
  const inRange = (id: number) => id >= base && id < base + 1000
  if (caps) return caps.categories.filter((c) => inRange(c.id))
  const ids = Object.keys(standardCategoryNames)
    .map(Number)
    .filter((id) => inRange(id) && id !== base)
  return [{ id: base, name: standardCategoryNames[base], subCategories: ids.map((id) => ({ id, name: standardCategoryNames[id], subCategories: [] })) }]
}

function IndexerDialog({ indexer, canEdit, onClose }: { indexer: Indexer; canEdit: boolean; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [draft, setDraft] = useState<Indexer>(indexer)
  const [caps, setCaps] = useState<IndexerCapabilities | null>(null)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const isNew = indexer.id === 0

  const done = async () => {
    await queryClient.invalidateQueries({ queryKey: indexerKeys.list })
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

  const update = (change: Partial<Indexer>) => setDraft((d) => ({ ...d, ...change }))

  const saveErrors = save.error instanceof ApiError ? save.error.fieldErrors : {}
  const testErrors = test.error instanceof ApiError ? test.error.fieldErrors : {}
  const fieldError = (name: string) => (saveErrors[name] ?? testErrors[name])?.[0]

  const submit = (event: FormEvent) => {
    event.preventDefault()
    save.mutate()
  }

  const status = indexer.status
  const title = isNew ? 'Add indexer' : canEdit ? `Edit ${indexer.name}` : indexer.name

  return (
    <Modal title={title} onClose={onClose}>
      <form className={styles.form} onSubmit={submit}>
        {status && !status.healthy && status.disabledTill && (
          <p className={styles.statusNote}>
            Skipped until {formatUntil(status.disabledTill)}
            {status.lastError ? `: ${status.lastError}` : ''}
          </p>
        )}
        <fieldset className={styles.fieldset} disabled={!canEdit}>
          <div className={styles.grid}>
            <label className={ui.field}>
              <span>Name</span>
              <input className={ui.input} value={draft.name} maxLength={100} onChange={(e) => update({ name: e.target.value })} autoFocus />
              {fieldError('name') && <small className={`${ui.error} ${styles.fieldError}`}>{fieldError('name')}</small>}
            </label>
            <label className={ui.field}>
              <span>Priority</span>
              <input
                className={ui.input}
                type="number"
                min={1}
                max={50}
                inputMode="numeric"
                value={Number.isNaN(draft.priority) ? '' : draft.priority}
                onChange={(e) => update({ priority: e.target.valueAsNumber })}
              />
              <small>1 is the highest. Breaks ties between equally good releases.</small>
              {fieldError('priority') && <small className={`${ui.error} ${styles.fieldError}`}>{fieldError('priority')}</small>}
            </label>
          </div>

          <label className={ui.field}>
            <span>URL</span>
            <input
              className={ui.input}
              type="url"
              inputMode="url"
              autoComplete="off"
              spellCheck={false}
              placeholder="https://indexer.example"
              value={draft.baseUrl}
              onChange={(e) => update({ baseUrl: e.target.value })}
            />
            {fieldError('baseUrl') && <small className={`${ui.error} ${styles.fieldError}`}>{fieldError('baseUrl')}</small>}
          </label>

          <div className={styles.grid}>
            <label className={ui.field}>
              <span>API path</span>
              <input
                className={`${ui.input} ${ui.mono}`}
                spellCheck={false}
                placeholder="/api"
                value={draft.apiPath}
                onChange={(e) => update({ apiPath: e.target.value })}
              />
              {fieldError('apiPath') && <small className={`${ui.error} ${styles.fieldError}`}>{fieldError('apiPath')}</small>}
            </label>
            <label className={ui.field}>
              <span>API key</span>
              <input
                className={ui.input}
                type="password"
                autoComplete="off"
                spellCheck={false}
                value={draft.apiKey}
                onFocus={(e) => draft.apiKey === maskedApiKey && e.target.select()}
                onChange={(e) => update({ apiKey: e.target.value })}
              />
              {draft.apiKey === maskedApiKey && <small>Saved. Type a new key to replace it.</small>}
              {fieldError('apiKey') && <small className={`${ui.error} ${styles.fieldError}`}>{fieldError('apiKey')}</small>}
            </label>
          </div>

          <div className={ui.field}>
            <span>Use for</span>
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
          </div>

          {canEdit && (
            <div className={styles.testRow}>
              <button className={ui.button} type="button" onClick={() => test.mutate()} disabled={test.isPending}>
                {test.isPending ? 'Testing…' : 'Test'}
              </button>
              {test.isSuccess && (
                <span className={ui.ok}>
                  Connection works{test.data.categories.length > 0 ? `, ${test.data.categories.length} category groups loaded` : ''}.
                </span>
              )}
              {test.error && Object.keys(testErrors).length === 0 && <span className={ui.error}>{test.error.message}</span>}
              {!test.isSuccess && !test.error && <span className={ui.muted}>Checks the connection and loads the indexer&apos;s categories.</span>}
            </div>
          )}

          <CategoryPicker
            legend="Movie categories"
            tree={categoryTree(caps, 2000)}
            selected={draft.categories}
            onChange={(categories) => update({ categories })}
          />
          <CategoryPicker
            legend="TV categories"
            tree={categoryTree(caps, 5000)}
            selected={draft.tvCategories}
            onChange={(tvCategories) => update({ tvCategories })}
          />
          {!caps && <small className={ui.muted}>Standard Newznab categories. Test to load the indexer&apos;s own list.</small>}
          {fieldError('categories') && <small className={`${ui.error} ${styles.fieldError}`}>{fieldError('categories')}</small>}
        </fieldset>

        {canEdit && (
          <div className={styles.footer}>
            <button className={ui.primary} type="submit" disabled={save.isPending}>
              {save.isPending ? 'Saving…' : 'Save'}
            </button>
            <button className={ui.button} type="button" onClick={onClose}>
              Cancel
            </button>
            <span className={styles.spacer} />
            {!isNew &&
              (confirmDelete ? (
                <>
                  <span className={ui.muted}>Delete this indexer?</span>
                  <button className={ui.danger} type="button" onClick={() => remove.mutate()} disabled={remove.isPending}>
                    Yes, delete
                  </button>
                  <button className={ui.button} type="button" onClick={() => setConfirmDelete(false)}>
                    Keep
                  </button>
                </>
              ) : (
                <button className={ui.button} type="button" onClick={() => setConfirmDelete(true)}>
                  Delete
                </button>
              ))}
          </div>
        )}
        {save.error && Object.keys(saveErrors).length === 0 && <p className={ui.error}>{save.error.message}</p>}
        {remove.error && <p className={ui.error}>{remove.error.message}</p>}
      </form>
    </Modal>
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
      <legend>{legend}</legend>
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
