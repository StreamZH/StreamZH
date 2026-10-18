import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState, type FormEvent } from 'react'
import { ApiError } from '../api/client'
import { profileKeys, profilesApi, type Language, type QualityDefinition, type QualityProfile } from '../api/profiles'
import { useAuth } from '../auth/auth'
import Icon from '../components/Icon'
import { Skeleton } from '../components/Loader'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import { SaveBar, Section, SettingsPage } from '../settings/SettingsKit'
import styles from './Profiles.module.css'

export default function Profiles() {
  const { user } = useAuth()
  const canEdit = user?.role === 'Owner' || user?.role === 'Admin'

  return (
    <SettingsPage title="Profiles" lead="Which qualities and languages StreamZH grabs, and until when it keeps upgrading. Size limits per quality are under Quality.">
      <ProfileList canEdit={canEdit} />
    </SettingsPage>
  )
}

function languageName(languages: Language[], id: number) {
  return languages.find((l) => l.id === id)?.name ?? 'Unknown'
}

function ProfileList({ canEdit }: { canEdit: boolean }) {
  const queryClient = useQueryClient()
  const profiles = useQuery({ queryKey: profileKeys.profiles, queryFn: profilesApi.profiles })
  const settings = useQuery({ queryKey: profileKeys.settings, queryFn: profilesApi.settings })
  const languages = useQuery({ queryKey: profileKeys.languages, queryFn: profilesApi.languages, staleTime: Infinity })
  const [editing, setEditing] = useState<QualityProfile | null>(null)

  const makeDefault = useMutation({
    mutationFn: profilesApi.saveSettings,
    onSuccess: (data) => queryClient.setQueryData(profileKeys.settings, data),
  })

  const create = useMutation({
    mutationFn: profilesApi.schema,
    onSuccess: (schema) => setEditing({ ...schema, name: '' }),
  })

  const defaultId = settings.data?.defaultQualityProfileId

  return (
    <Section
      id="quality-profiles"
      title="Quality profiles"
      description="Each movie and series uses one profile. New items get the default profile unless you choose another one."
      actions={
        canEdit && (
          <button className={`${ui.primary} ${ui.sm}`} type="button" onClick={() => create.mutate()} disabled={create.isPending}>
            <Icon name="plus" size={14} />
            New profile
          </button>
        )
      }
    >
      {profiles.isPending && <Skeleton rows={4} />}
      {profiles.isError && <p className={ui.error}>{profiles.error.message}</p>}
      {create.error && <p className={ui.error}>{create.error.message}</p>}
      {makeDefault.error && <p className={ui.error}>{makeDefault.error.message}</p>}

      <ul className={styles.list}>
        {(profiles.data ?? []).map((p) => {
          const allowed = p.items.filter((i) => i.allowed).length
          const cutoff = p.items.find((i) => i.qualityId === p.cutoffQualityId)?.name ?? '–'
          return (
            <li key={p.id} className={styles.row}>
              <div className={styles.rowMain}>
                <span className={`${styles.rowIcon} ${p.id === defaultId ? styles.rowIconOn : ''}`} aria-hidden="true">
                  <Icon name="star" size={16} />
                </span>
                <span className={styles.name}>{p.name}</span>
                {p.id === defaultId && <span className={`${ui.pill} ${ui.pillOk}`}>Default</span>}
              </div>
              <dl className={styles.meta}>
                <div>
                  <dt>Language</dt>
                  <dd>
                    {languageName(languages.data ?? [], p.languageId)}
                    {p.preferDualLanguage ? ' · dual' : ''}
                  </dd>
                </div>
                <div>
                  <dt>Cutoff</dt>
                  <dd>{p.upgradeAllowed ? cutoff : 'No upgrades'}</dd>
                </div>
                <div>
                  <dt>Qualities</dt>
                  <dd>{allowed} allowed</dd>
                </div>
              </dl>
              <div className={styles.rowActions}>
                {canEdit && p.id !== defaultId && (
                  <button className={`${ui.ghost} ${ui.sm}`} type="button" onClick={() => makeDefault.mutate(p.id)} disabled={makeDefault.isPending}>
                    Set as default
                  </button>
                )}
                <button className={`${ui.button} ${ui.sm}`} type="button" onClick={() => setEditing(p)}>
                  <Icon name={canEdit ? 'edit' : 'eye'} size={14} />
                  {canEdit ? 'Edit' : 'View'}
                </button>
              </div>
            </li>
          )
        })}
      </ul>

      {editing && (
        <ProfileDialog
          profile={editing}
          languages={languages.data ?? []}
          canEdit={canEdit}
          isDefault={editing.id === defaultId}
          onClose={() => setEditing(null)}
        />
      )}
    </Section>
  )
}

function ProfileDialog({
  profile,
  languages,
  canEdit,
  isDefault,
  onClose,
}: {
  profile: QualityProfile
  languages: Language[]
  canEdit: boolean
  isDefault: boolean
  onClose: () => void
}) {
  const queryClient = useQueryClient()
  const [draft, setDraft] = useState<QualityProfile>(profile)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const pendingFocus = useRef<{ qualityId: number; dir: 'up' | 'down' } | null>(null)
  const listRef = useRef<HTMLOListElement>(null)
  const isNew = profile.id === 0

  const done = async () => {
    await queryClient.invalidateQueries({ queryKey: profileKeys.profiles })
    await queryClient.invalidateQueries({ queryKey: profileKeys.settings })
    onClose()
  }

  const save = useMutation({
    mutationFn: () => (isNew ? profilesApi.create(draft) : profilesApi.update(draft)),
    onSuccess: done,
  })
  const remove = useMutation({ mutationFn: () => profilesApi.remove(profile.id), onSuccess: done })

  // Keep keyboard focus on the move button after the row moved.
  useEffect(() => {
    const focus = pendingFocus.current
    pendingFocus.current = null
    if (!focus || !listRef.current) return
    const row = listRef.current.querySelector<HTMLElement>(`[data-quality="${focus.qualityId}"]`)
    const wanted = row?.querySelector<HTMLButtonElement>(`[data-move="${focus.dir}"]`)
    const other = row?.querySelector<HTMLButtonElement>(`[data-move="${focus.dir === 'up' ? 'down' : 'up'}"]`)
    ;(wanted && !wanted.disabled ? wanted : other)?.focus()
  }, [draft.items])

  const fieldErrors = save.error instanceof ApiError ? save.error.fieldErrors : {}
  const allowedItems = draft.items.filter((i) => i.allowed)
  // Shown best first; stored worst first.
  const shown = [...draft.items].reverse()

  const update = (change: Partial<QualityProfile>) => setDraft((d) => ({ ...d, ...change }))

  const toggle = (qualityId: number, allowed: boolean) =>
    setDraft((d) => {
      const items = d.items.map((i) => (i.qualityId === qualityId ? { ...i, allowed } : i))
      const stillAllowed = items.some((i) => i.allowed && i.qualityId === d.cutoffQualityId)
      // Keep the cutoff valid: fall back to the best allowed quality.
      const best = [...items].reverse().find((i) => i.allowed)
      return { ...d, items, cutoffQualityId: stillAllowed ? d.cutoffQualityId : (best?.qualityId ?? 0) }
    })

  /** Moves a quality one step better (up) or worse (down). */
  const move = (qualityId: number, dir: 'up' | 'down') => {
    setDraft((d) => {
      const index = d.items.findIndex((i) => i.qualityId === qualityId)
      const target = dir === 'up' ? index + 1 : index - 1
      if (index < 0 || target < 0 || target >= d.items.length) return d
      const items = [...d.items]
      ;[items[index], items[target]] = [items[target], items[index]]
      return { ...d, items }
    })
    pendingFocus.current = { qualityId, dir }
  }

  const submit = (event: FormEvent) => {
    event.preventDefault()
    save.mutate()
  }

  return (
    <Modal title={isNew ? 'New profile' : canEdit ? `Edit ${profile.name}` : profile.name} onClose={onClose}>
      <form className={styles.form} onSubmit={submit}>
        <fieldset className={styles.fieldset} disabled={!canEdit}>
          <div className={styles.grid}>
            <label className={ui.field}>
              <span>Name</span>
              <input className={ui.input} value={draft.name} maxLength={100} onChange={(e) => update({ name: e.target.value })} autoFocus />
              {fieldErrors.name && <small className={ui.error}>{fieldErrors.name[0]}</small>}
            </label>
            <label className={ui.field}>
              <span>Language</span>
              <select className={ui.select} value={draft.languageId} onChange={(e) => update({ languageId: Number(e.target.value) })}>
                {languages.map((l) => (
                  <option key={l.id} value={l.id}>
                    {l.id === -1 ? 'Any language' : l.id === -2 ? 'Original language' : l.name}
                  </option>
                ))}
              </select>
              {fieldErrors.languageId && <small className={ui.error}>{fieldErrors.languageId[0]}</small>}
            </label>
          </div>

          <div className={styles.checks}>
            <label className={ui.check}>
              <input type="checkbox" checked={draft.preferDualLanguage} onChange={(e) => update({ preferDualLanguage: e.target.checked })} />
              <span>Prefer dual language (e.g. German.DL)</span>
            </label>
            <label className={ui.check}>
              <input type="checkbox" checked={draft.upgradeAllowed} onChange={(e) => update({ upgradeAllowed: e.target.checked })} />
              <span>Upgrades allowed</span>
            </label>
          </div>

          <label className={ui.field}>
            <span>Upgrade until (cutoff)</span>
            <select
              className={ui.select}
              value={allowedItems.some((i) => i.qualityId === draft.cutoffQualityId) ? draft.cutoffQualityId : ''}
              onChange={(e) => update({ cutoffQualityId: Number(e.target.value) })}
              disabled={allowedItems.length === 0 || !draft.upgradeAllowed}
            >
              {allowedItems.length === 0 && <option value="">Allow a quality first</option>}
              {[...allowedItems].reverse().map((i) => (
                <option key={i.qualityId} value={i.qualityId}>
                  {i.name}
                </option>
              ))}
            </select>
            <small>Once a file has this quality, StreamZH stops looking for better releases.</small>
            {fieldErrors.cutoffQualityId && <small className={ui.error}>{fieldErrors.cutoffQualityId[0]}</small>}
          </label>

          <div className={ui.field}>
            <span id="qualities-label">Qualities</span>
            <small>Best first. Higher in the list is preferred; only checked qualities are grabbed.</small>
            <ol className={styles.qualities} ref={listRef} aria-labelledby="qualities-label">
              {shown.map((item, index) => (
                <li key={item.qualityId} data-quality={item.qualityId} className={item.allowed ? undefined : styles.notAllowed}>
                  <label className={ui.check}>
                    <input type="checkbox" checked={item.allowed} onChange={(e) => toggle(item.qualityId, e.target.checked)} />
                    <span>{item.name}</span>
                  </label>
                  {item.qualityId === draft.cutoffQualityId && item.allowed && <span className={`${ui.tag} ${styles.cutoffTag}`}>Cutoff</span>}
                  {canEdit && (
                    <span className={styles.moveButtons}>
                      <button
                        type="button"
                        className={`${ui.ghost} ${ui.sm} ${ui.icon}`}
                        data-move="up"
                        onClick={() => move(item.qualityId, 'up')}
                        disabled={index === 0}
                        aria-label={`Move ${item.name} up`}
                      >
                        <Icon name="up" size={15} />
                      </button>
                      <button
                        type="button"
                        className={`${ui.ghost} ${ui.sm} ${ui.icon}`}
                        data-move="down"
                        onClick={() => move(item.qualityId, 'down')}
                        disabled={index === shown.length - 1}
                        aria-label={`Move ${item.name} down`}
                      >
                        <Icon name="down" size={15} />
                      </button>
                    </span>
                  )}
                </li>
              ))}
            </ol>
            {fieldErrors.items && <small className={ui.error}>{fieldErrors.items[0]}</small>}
          </div>
        </fieldset>

        {canEdit && (
          <div className={styles.footer}>
            {!isNew &&
              (confirmDelete ? (
                <>
                  <span className={ui.muted}>{isDefault ? 'This is the default profile.' : 'Delete this profile?'}</span>
                  <button className={ui.danger} type="button" onClick={() => remove.mutate()} disabled={remove.isPending}>
                    Yes, delete
                  </button>
                  <button className={ui.button} type="button" onClick={() => setConfirmDelete(false)}>
                    Keep
                  </button>
                </>
              ) : (
                <button className={ui.ghost} type="button" onClick={() => setConfirmDelete(true)}>
                  <Icon name="trash" size={15} />
                  Delete
                </button>
              ))}
            <span className={styles.spacer} />
            <button className={ui.button} type="button" onClick={onClose}>
              Cancel
            </button>
            <button className={ui.primary} type="submit" disabled={save.isPending}>
              {save.isPending ? 'Saving…' : 'Save'}
            </button>
          </div>
        )}
        {save.error && Object.keys(fieldErrors).length === 0 && <p className={ui.error}>{save.error.message}</p>}
        {remove.error && <p className={ui.error}>{remove.error.message}</p>}
      </form>
    </Modal>
  )
}

type DefinitionDraft = { qualityId: number; title: string; minSize: string; maxSize: string; preferredSize: string }

const toDraft = (d: QualityDefinition): DefinitionDraft => ({
  qualityId: d.qualityId,
  title: d.title,
  minSize: String(d.minSize),
  maxSize: d.maxSize === null ? '' : String(d.maxSize),
  preferredSize: d.preferredSize === null ? '' : String(d.preferredSize),
})

const toNumber = (value: string) => (value.trim() === '' ? null : Number(value))

export function SizeLimits({ canEdit, defaultOpen = false }: { canEdit: boolean; defaultOpen?: boolean }) {
  const queryClient = useQueryClient()
  const [open, setOpen] = useState(defaultOpen)
  const definitions = useQuery({ queryKey: profileKeys.definitions, queryFn: profilesApi.definitions, enabled: open })
  const [drafts, setDrafts] = useState<DefinitionDraft[] | null>(null)
  const [saved, setSaved] = useState(false)
  const rows = drafts ?? (definitions.data ?? []).map(toDraft)

  const save = useMutation({
    mutationFn: () =>
      profilesApi.saveDefinitions(
        rows.map((r) => ({
          qualityId: r.qualityId,
          title: r.title,
          minSize: toNumber(r.minSize) ?? 0,
          maxSize: toNumber(r.maxSize),
          preferredSize: toNumber(r.preferredSize),
        })),
      ),
    onSuccess: (data) => {
      queryClient.setQueryData(profileKeys.definitions, data)
      setDrafts(null)
      setSaved(true)
    },
  })

  useEffect(() => {
    if (!saved) return
    const timer = setTimeout(() => setSaved(false), 2500)
    return () => clearTimeout(timer)
  }, [saved])

  const fieldErrors = save.error instanceof ApiError ? save.error.fieldErrors : {}
  const change = (index: number, field: keyof DefinitionDraft, value: string) =>
    setDrafts(rows.map((r, i) => (i === index ? { ...r, [field]: value } : r)))

  // Scale of the small range bars: the largest limit that is set, with some room.
  const limits = rows.flatMap((r) => [r.minSize, r.preferredSize, r.maxSize].map(toNumber).filter((n): n is number => n != null && Number.isFinite(n)))
  const scale = Math.max(10, ...limits) * 1.08
  const pct = (value: number) => `${Math.min(100, Math.max(0, (value / scale) * 100))}%`

  return (
    <section className={`${ui.panel} ${styles.sizePanel}`}>
      <button type="button" className={styles.collapse} onClick={() => setOpen((o) => !o)} aria-expanded={open} aria-controls="size-limits">
        <span className={styles.collapseText}>
          <span className={styles.collapseTitle}>Quality size limits</span>
          <span className={styles.collapseDesc}>
            In MB per minute of runtime. Releases outside the limits are skipped; StreamZH prefers sizes close to the preferred value. Leave maximum or
            preferred empty for no limit.
          </span>
        </span>
        <Icon name="down" size={16} className={`${styles.chevron} ${open ? styles.chevronOpen : ''}`} />
      </button>
      {open && (
        <div id="size-limits" className={styles.sizeBody}>
          {definitions.isError && <p className={ui.error}>{definitions.error.message}</p>}
          {definitions.isPending && <Skeleton rows={6} />}
          <form
            onSubmit={(e) => {
              e.preventDefault()
              save.mutate()
            }}
          >
            <div className={styles.tableWrap}>
              <table className={styles.sizes}>
                <thead>
                  <tr>
                    <th>Quality</th>
                    <th className={styles.rangeCol} aria-hidden="true">
                      Range
                    </th>
                    <th>Min</th>
                    <th>Preferred</th>
                    <th>Max</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((r, index) => {
                    const error = ['minSize', 'preferredSize', 'maxSize', 'qualityId']
                      .map((f) => fieldErrors[`[${index}].${f}`]?.[0])
                      .find(Boolean)
                    const min = toNumber(r.minSize) ?? 0
                    const max = toNumber(r.maxSize)
                    const preferred = toNumber(r.preferredSize)
                    return (
                      <tr key={r.qualityId} className={error ? styles.sizeInvalid : undefined}>
                        <th scope="row">
                          {r.title}
                          {error && <small className={ui.error}>{error}</small>}
                        </th>
                        <td className={styles.rangeCol} aria-hidden="true">
                          <span className={styles.range}>
                            <i
                              className={`${styles.rangeFill} ${max == null ? styles.rangeOpen : ''}`}
                              style={{ left: pct(min), right: max == null ? '0%' : `calc(100% - ${pct(max)})` }}
                            />
                            {preferred != null && <b className={styles.rangeMark} style={{ left: pct(preferred) }} />}
                          </span>
                        </td>
                        {(['minSize', 'preferredSize', 'maxSize'] as const).map((field) => (
                          <td key={field}>
                            <input
                              className={`${ui.input} ${ui.num}`}
                              type="number"
                              min={0}
                              step="any"
                              inputMode="decimal"
                              placeholder={field === 'minSize' ? '0' : 'No limit'}
                              value={r[field]}
                              onChange={(e) => change(index, field, e.target.value)}
                              disabled={!canEdit}
                              aria-label={`${r.title} ${field === 'minSize' ? 'minimum' : field === 'maxSize' ? 'maximum' : 'preferred'} MB per minute`}
                            />
                          </td>
                        ))}
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
            {canEdit && (
              <SaveBar
                dirty={!!drafts}
                saving={save.isPending}
                saved={saved}
                error={
                  Object.keys(fieldErrors).length > 0 ? 'Some limits are not valid.' : save.error ? save.error.message : null
                }
                onSave={() => save.mutate()}
                onReset={() => {
                  setDrafts(null)
                  save.reset()
                }}
              />
            )}
          </form>
        </div>
      )}
    </section>
  )
}
