import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { ApiError } from '../api/client'
import { customFormatKeys } from '../api/customFormats'
import { profileKeys, profilesApi, type Language, type QualityProfile } from '../api/profiles'
import Icon from '../components/Icon'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import { AdvancedToggle } from '../lib/Advanced'
import { useAdvanced } from '../lib/advancedMode'
import styles from './ProfileDialog.module.css'
import ProfileFormats from './ProfileFormats'
import QualityList from './QualityList'
import { blockId, blockName, toBlocks, validCutoff } from './qualityBlocks'
import { changedScores, useTemplateNames, useTemplateScores } from './trashTemplates'

type Tab = 'qualities' | 'formats'

/** Create, edit or view a quality profile: qualities (with groups) and custom format scores. */
export default function ProfileDialog({
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
  const advanced = useAdvanced()
  const [draft, setDraft] = useState<QualityProfile>(profile)
  const [tab, setTab] = useState<Tab>('qualities')
  const [confirmDelete, setConfirmDelete] = useState(false)
  const isNew = profile.id === 0
  const hasFormats = profile.formatItems !== undefined
  const managed = !!profile.trashMovieTemplate || !!profile.trashSeriesTemplate
  const templateNames = useTemplateNames(managed)
  const template = useTemplateScores(profile)

  const done = async () => {
    await queryClient.invalidateQueries({ queryKey: profileKeys.profiles })
    await queryClient.invalidateQueries({ queryKey: profileKeys.settings })
    await queryClient.invalidateQueries({ queryKey: customFormatKeys.all })
    onClose()
  }

  const save = useMutation({
    mutationFn: () => (isNew ? profilesApi.create(draft) : profilesApi.update(draft)),
    onSuccess: done,
  })
  const remove = useMutation({ mutationFn: () => profilesApi.remove(profile.id), onSuccess: done })

  const fieldErrors = save.error instanceof ApiError ? save.error.fieldErrors : {}
  const formatErrors = Object.keys(fieldErrors).some((k) => /format/i.test(k))
  const update = (change: Partial<QualityProfile>) => setDraft((d) => ({ ...d, ...change }))
  const allowedBlocks = toBlocks(draft.items).filter((b) => b.items.some((i) => i.allowed))
  const cutoffBlock = allowedBlocks.find((b) => b.items.some((i) => i.qualityId === draft.cutoffQualityId))
  const scored = (draft.formatItems ?? []).filter((i) => i.score !== 0).length
  const changed = changedScores(draft, template).length

  const submit = (event: FormEvent) => {
    event.preventDefault()
    save.mutate()
  }

  const templates = [profile.trashMovieTemplate, profile.trashSeriesTemplate].filter((t): t is string => !!t)
  const templateLabels = [...new Set(templates.map((t) => templateNames.get(t) ?? 'template'))]

  return (
    <Modal title={isNew ? 'New profile' : canEdit ? `Edit ${profile.name}` : profile.name} onClose={onClose}>
      <form className={`${styles.form} ${styles.profileDialog}`} onSubmit={submit}>
        {managed && (
          <div className={styles.trashLine}>
            {templateLabels.map((name) => (
              <span key={name} className={styles.trashPill}>
                <Icon name="sparkle" size={12} />
                TRaSH: {name}
              </span>
            ))}
            {changed > 0 && (
              <span className={styles.changedPill}>
                <span className={styles.changedDot} />
                {changed} score{changed === 1 ? '' : 's'} changed by you
              </span>
            )}
          </div>
        )}
        <fieldset className={styles.fieldset} disabled={!canEdit}>
          <div className={styles.grid}>
            <label className={ui.field}>
              <span>Name</span>
              <input className={ui.input} value={draft.name} maxLength={100} onChange={(e) => update({ name: e.target.value })} autoFocus={isNew} />
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

          <div className={styles.tabBar}>
            {hasFormats ? (
              <div className={styles.tabs} role="tablist" aria-label="Profile parts">
                <button type="button" role="tab" aria-selected={tab === 'qualities'} onClick={() => setTab('qualities')}>
                  Qualities
                  <span className={styles.tabCount}>{draft.items.filter((i) => i.allowed).length}</span>
                </button>
                <button type="button" role="tab" aria-selected={tab === 'formats'} onClick={() => setTab('formats')}>
                  Custom formats
                  <span className={`${styles.tabCount} ${formatErrors ? styles.tabCountErr : ''}`}>{scored}</span>
                </button>
              </div>
            ) : (
              <span />
            )}
            <AdvancedToggle />
          </div>

          {tab === 'qualities' || !hasFormats ? (
            <div className={styles.tabBody} role={hasFormats ? 'tabpanel' : undefined}>
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
                  value={cutoffBlock ? blockId(cutoffBlock) : ''}
                  onChange={(e) => update({ cutoffQualityId: Number(e.target.value) })}
                  disabled={allowedBlocks.length === 0 || !draft.upgradeAllowed}
                >
                  {allowedBlocks.length === 0 && <option value="">Allow a quality first</option>}
                  {[...allowedBlocks].reverse().map((b) => (
                    <option key={b.key} value={blockId(b)}>
                      {blockName(b)}
                    </option>
                  ))}
                </select>
                <small>Once a file has this quality, StreamZH stops looking for better releases.</small>
                {fieldErrors.cutoffQualityId && <small className={ui.error}>{fieldErrors.cutoffQualityId[0]}</small>}
              </label>

              <div className={ui.field}>
                <span>Qualities</span>
                <small>
                  Best first. Higher is preferred; only checked qualities are grabbed.
                  {advanced ? ' Select two or more to group them: grouped qualities rank equally and the format score decides.' : ''}
                </small>
                <QualityList
                  items={draft.items}
                  cutoffQualityId={draft.cutoffQualityId}
                  canEdit={canEdit}
                  advanced={advanced}
                  onChange={(items) => setDraft((d) => ({ ...d, items, cutoffQualityId: validCutoff(items, d.cutoffQualityId) }))}
                />
                {fieldErrors.items && <small className={ui.error}>{fieldErrors.items[0]}</small>}
              </div>
            </div>
          ) : (
            <div className={styles.tabBody} role="tabpanel">
              <ProfileFormats draft={draft} update={update} canEdit={canEdit} advanced={advanced} template={template} errors={fieldErrors} />
            </div>
          )}
        </fieldset>

        {save.error && Object.keys(fieldErrors).length === 0 && <p className={ui.errBox}>{save.error.message}</p>}
        {formatErrors && tab !== 'formats' && <p className={ui.error}>Check the custom format scores.</p>}
        {remove.error && <p className={ui.errBox}>{remove.error.message}</p>}

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
      </form>
    </Modal>
  )
}
