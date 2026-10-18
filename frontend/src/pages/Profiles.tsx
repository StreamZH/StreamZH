import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { ApiError } from '../api/client'
import { profileKeys, profilesApi, type Language, type QualityDefinition, type QualityProfile } from '../api/profiles'
import { useAuth } from '../auth/auth'
import Icon from '../components/Icon'
import { Skeleton } from '../components/Loader'
import ui from '../components/ui.module.css'
import ProfileDialog from '../profiles/ProfileDialog'
import { toBlocks } from '../profiles/qualityBlocks'
import { changedScores, useTemplateNames, useTemplateScores } from '../profiles/trashTemplates'
import { SaveBar, Section, SettingsPage } from '../settings/SettingsKit'
import TrashSection, { UpdateFromTrashDialog } from '../trash/TrashSection'
import styles from './Profiles.module.css'

export default function Profiles() {
  const { user } = useAuth()
  const canEdit = user?.role === 'Owner' || user?.role === 'Admin'

  return (
    <SettingsPage title="Profiles" lead="Which qualities and languages StreamZH grabs, how it scores custom formats, and until when it keeps upgrading. Size limits per quality are under Quality.">
      <TrashSection canEdit={canEdit} />
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
  const [updating, setUpdating] = useState<QualityProfile | null>(null)
  const managed = (profiles.data ?? []).some((p) => p.trashMovieTemplate || p.trashSeriesTemplate)
  const templateNames = useTemplateNames(managed)

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
          <button className={`${ui.button} ${ui.sm}`} type="button" onClick={() => create.mutate()} disabled={create.isPending}>
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
        {(profiles.data ?? []).map((p) => (
          <ProfileRow
            key={p.id}
            profile={p}
            languages={languages.data ?? []}
            templateNames={templateNames}
            isDefault={p.id === defaultId}
            canEdit={canEdit}
            busy={makeDefault.isPending}
            onDefault={() => makeDefault.mutate(p.id)}
            onEdit={() => setEditing(p)}
            onUpdate={() => setUpdating(p)}
          />
        ))}
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
      {updating && <UpdateFromTrashDialog profile={updating} onClose={() => setUpdating(null)} />}
    </Section>
  )
}

function ProfileRow({
  profile: p,
  languages,
  templateNames,
  isDefault,
  canEdit,
  busy,
  onDefault,
  onEdit,
  onUpdate,
}: {
  profile: QualityProfile
  languages: Language[]
  templateNames: Map<string, string>
  isDefault: boolean
  canEdit: boolean
  busy: boolean
  onDefault: () => void
  onEdit: () => void
  onUpdate: () => void
}) {
  const template = useTemplateScores(p)
  const changed = changedScores(p, template).length
  const blocks = toBlocks(p.items)
  const cutoffBlock = blocks.find((b) => b.items.some((i) => i.qualityId === p.cutoffQualityId))
  const cutoff = cutoffBlock ? (cutoffBlock.group ?? cutoffBlock.items[0].name) : '–'
  const scored = (p.formatItems ?? []).filter((i) => i.score !== 0).length
  const templates = [p.trashMovieTemplate, p.trashSeriesTemplate].filter((t): t is string => !!t)
  const names = [...new Set(templates.map((t) => templateNames.get(t)).filter((n): n is string => !!n))]
  const managed = templates.length > 0

  return (
    <li className={styles.row}>
      <div className={styles.rowMain}>
        <span className={`${styles.rowIcon} ${isDefault ? styles.rowIconOn : ''}`} aria-hidden="true">
          <Icon name="star" size={16} />
        </span>
        <span className={styles.rowTitle}>
          <span className={styles.nameLine}>
            <span className={styles.name}>{p.name}</span>
            {isDefault && <span className={`${ui.pill} ${ui.pillOk}`}>Default</span>}
          </span>
          {managed && (
            <span className={styles.trashLine}>
              <span className={styles.trashPill}>
                <Icon name="sparkle" size={11} />
                TRaSH: {names.length > 0 ? names.join(' · ') : 'template'}
              </span>
              {changed > 0 && (
                <span className={styles.changedText} title="Scores that differ from the TRaSH template">
                  {changed} score{changed === 1 ? '' : 's'} changed
                </span>
              )}
            </span>
          )}
        </span>
      </div>
      <dl className={styles.meta}>
        <div>
          <dt>Language</dt>
          <dd>
            {languageName(languages, p.languageId)}
            {p.preferDualLanguage ? ' · dual' : ''}
          </dd>
        </div>
        <div>
          <dt>Cutoff</dt>
          <dd>{p.upgradeAllowed ? cutoff : 'No upgrades'}</dd>
        </div>
        <div>
          <dt>Qualities</dt>
          <dd>{p.items.filter((i) => i.allowed).length} allowed</dd>
        </div>
        {p.formatItems && (
          <div>
            <dt>Formats</dt>
            <dd>{scored > 0 ? `${scored} scored` : 'none scored'}</dd>
          </div>
        )}
      </dl>
      <div className={styles.rowActions}>
        {canEdit && !isDefault && (
          <button className={`${ui.ghost} ${ui.sm}`} type="button" onClick={onDefault} disabled={busy}>
            Set as default
          </button>
        )}
        {canEdit && managed && (
          <button className={`${ui.ghost} ${ui.sm}`} type="button" onClick={onUpdate} title="Apply the current TRaSH template to this profile again">
            <Icon name="restart" size={14} />
            Update
          </button>
        )}
        <button className={`${ui.button} ${ui.sm}`} type="button" onClick={onEdit}>
          <Icon name={canEdit ? 'edit' : 'eye'} size={14} />
          {canEdit ? 'Edit' : 'View'}
        </button>
      </div>
    </li>
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
