import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { createPortal } from 'react-dom'
import { ApiError } from '../api/client'
import { checkTerm, indexerOptionsApi, indexerOptionsKeys, type ReleaseProfile } from '../api/indexerOptions'
import { indexerKeys, indexersApi } from '../api/indexers'
import { profileKeys, profilesApi } from '../api/profiles'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import styles from './IndexerOptions.module.css'
import { Row, Toggle } from './SettingsKit'
import { PreferredInput, TermInput } from './TermInput'

/** Create or edit a release profile (Sonarr v3 style). */
export default function ReleaseProfileDialog({ profile, onClose }: { profile: ReleaseProfile; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [draft, setDraft] = useState<ReleaseProfile>(profile)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const indexers = useQuery({ queryKey: indexerKeys.list, queryFn: indexersApi.list })
  const qualityProfiles = useQuery({ queryKey: profileKeys.profiles, queryFn: profilesApi.profiles })

  const done = async () => {
    await queryClient.invalidateQueries({ queryKey: indexerOptionsKeys.releaseProfiles })
    onClose()
  }
  const save = useMutation({ mutationFn: () => indexerOptionsApi.saveReleaseProfile(draft), onSuccess: done })
  const remove = useMutation({ mutationFn: () => indexerOptionsApi.removeReleaseProfile(profile.id), onSuccess: done })

  const set = (change: Partial<ReleaseProfile>) => {
    save.reset()
    setDraft((d) => ({ ...d, ...change }))
  }

  const fieldErrors = save.error instanceof ApiError ? save.error.fieldErrors : {}
  const error = (field: string) => fieldErrors[field]?.[0]
  const formError = save.error && Object.keys(fieldErrors).length === 0 ? save.error.message : null
  const termCount = draft.required.length + draft.ignored.length + draft.preferred.length
  const hasProblem =
    [...draft.required, ...draft.ignored, ...draft.preferred.map((p) => p.term)].some((t) => checkTerm(t) !== null) ||
    draft.preferred.some((p) => p.score === 0 || Math.abs(p.score) > 10000)
  const valid = draft.name.trim().length > 0 && termCount > 0 && !hasProblem

  const toggleId = (list: number[], id: number) => (list.includes(id) ? list.filter((x) => x !== id) : [...list, id])

  const submit = (event: FormEvent) => {
    event.preventDefault()
    if (valid) save.mutate()
  }

  return createPortal(
    <Modal title={profile.id > 0 ? 'Edit release profile' : 'Add release profile'} onClose={onClose}>
      <form className={styles.dialogForm} onSubmit={submit}>
        <Row label="Name" htmlFor="rp-name" error={error('name')}>
          <input id="rp-name" className={ui.input} value={draft.name} maxLength={100} onChange={(e) => set({ name: e.target.value })} autoFocus />
        </Row>
        <Row label="Enabled">
          <Toggle checked={draft.enabled} onChange={(enabled) => set({ enabled })} />
        </Row>
        <Row
          label="Must contain"
          htmlFor="rp-required"
          help={
            <>
              The release must contain at least one of these terms. Plain text ignores case; <code>/regex/</code> is case-sensitive,{' '}
              <code>/regex/i</code> is not.
            </>
          }
          error={error('required')}
        >
          <TermInput id="rp-required" label="Must contain" value={draft.required} onChange={(required) => set({ required })} />
        </Row>
        <Row label="Must not contain" htmlFor="rp-ignored" help="The release is rejected when it contains any of these terms." error={error('ignored')}>
          <TermInput id="rp-ignored" label="Must not contain" value={draft.ignored} onChange={(ignored) => set({ ignored })} />
        </Row>
        <Row
          label="Preferred"
          help="Matching terms add their score. A higher total ranks a release higher after quality and language, and can replace a file of the same quality when the profile allows upgrades."
          error={error('preferred')}
        >
          <PreferredInput value={draft.preferred} onChange={(preferred) => set({ preferred })} />
        </Row>
        <Row label="Indexers" help="None selected: all indexers." error={error('indexerIds')}>
          <div className={styles.picks}>
            {(indexers.data ?? []).map((i) => (
              <label key={i.id} className={ui.check}>
                <input type="checkbox" checked={draft.indexerIds.includes(i.id)} onChange={() => set({ indexerIds: toggleId(draft.indexerIds, i.id) })} />
                <span>{i.name}</span>
              </label>
            ))}
            {indexers.data?.length === 0 && <span className={ui.muted}>No indexers yet.</span>}
          </div>
        </Row>
        <Row label="Quality profiles" help="None selected: all quality profiles." error={error('qualityProfileIds')}>
          <div className={styles.picks}>
            {(qualityProfiles.data ?? []).map((p) => (
              <label key={p.id} className={ui.check}>
                <input
                  type="checkbox"
                  checked={draft.qualityProfileIds.includes(p.id)}
                  onChange={() => set({ qualityProfileIds: toggleId(draft.qualityProfileIds, p.id) })}
                />
                <span>{p.name}</span>
              </label>
            ))}
          </div>
        </Row>

        <div className={styles.dialogActions}>
          <span>
            {profile.id > 0 &&
              (confirmDelete ? (
                <span className={styles.picks}>
                  <button className={ui.danger} type="button" onClick={() => remove.mutate()} disabled={remove.isPending}>
                    Delete for good
                  </button>
                  <button className={ui.button} type="button" onClick={() => setConfirmDelete(false)}>
                    Keep
                  </button>
                </span>
              ) : (
                <button className={ui.button} type="button" onClick={() => setConfirmDelete(true)}>
                  Delete
                </button>
              ))}
          </span>
          {formError && <p className={styles.formError}>{formError}</p>}
          {remove.error && <p className={styles.formError}>{remove.error.message}</p>}
          {termCount === 0 && <span className={ui.muted}>Add at least one term.</span>}
          <button className={ui.button} type="button" onClick={onClose}>
            Cancel
          </button>
          <button className={ui.primary} type="submit" disabled={!valid || save.isPending}>
            {save.isPending ? 'Saving…' : 'Save'}
          </button>
        </div>
      </form>
    </Modal>,
    document.body,
  )
}
