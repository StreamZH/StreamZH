import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { ApiError } from '../api/client'
import { customFormatKeys } from '../api/customFormats'
import { profileKeys, type QualityProfile } from '../api/profiles'
import { trashApi, trashKeys, type TrashSettings } from '../api/trash'
import Icon from '../components/Icon'
import Modal from '../components/Modal'
import { AdvancedHint } from '../lib/Advanced'
import ui from '../components/ui.module.css'
import { formatAgo, formatDateTime } from '../lib/format'
import { NumberInput, Row, SaveBar, Section, Toggle } from '../settings/SettingsKit'
import { useSettingsForm } from '../settings/useSettingsForm'
import RecyclarrDialog from './RecyclarrDialog'
import styles from './Trash.module.css'
import TrashWizard from './TrashWizard'

/**
 * «TRaSH Guides» on top of Settings → Profiles (the Recyclarr part): sync status, sync now, profiles from templates,
 * recyclarr.yml import and the sync settings.
 */
export default function TrashSection({ canEdit }: { canEdit: boolean }) {
  const queryClient = useQueryClient()
  const status = useQuery({ queryKey: trashKeys.status, queryFn: trashApi.status, retry: false, refetchInterval: 60_000 })
  const [wizard, setWizard] = useState(false)
  const [recyclarr, setRecyclarr] = useState(false)
  const [settingsOpen, setSettingsOpen] = useState(false)

  const sync = useMutation({
    mutationFn: trashApi.sync,
    onSuccess: async (data) => {
      queryClient.setQueryData(trashKeys.status, data)
      await queryClient.invalidateQueries({ queryKey: ['trash-profiles'] })
      await queryClient.invalidateQueries({ queryKey: ['trash-profile'] })
      await queryClient.invalidateQueries({ queryKey: ['trash-formats'] })
      await queryClient.invalidateQueries({ queryKey: ['trash-groups'] })
      await queryClient.invalidateQueries({ queryKey: customFormatKeys.all })
      await queryClient.invalidateQueries({ queryKey: profileKeys.profiles })
    },
  })

  // A server without the TRaSH part (older build): leave the section out.
  if (status.error instanceof ApiError && status.error.status === 404) return null

  const s = status.data
  const counts = s?.counts
  const error = sync.error?.message ?? s?.error ?? null

  return (
    <Section
      id="trash-guides"
      title="TRaSH Guides"
      description="Ready-made profiles and custom formats from the TRaSH Guides, kept up to date. This is what Recyclarr does, built in."
      actions={
        canEdit && (
          <>
            <button className={`${ui.ghost} ${ui.sm}`} type="button" onClick={() => setRecyclarr(true)}>
              <Icon name="upload" size={14} />
              Import recyclarr.yml
            </button>
            <button className={`${ui.primary} ${ui.sm}`} type="button" onClick={() => setWizard(true)} disabled={s ? !s.enabled : false}>
              <Icon name="sparkle" size={14} />
              Create profile from TRaSH
            </button>
          </>
        )
      }
    >
      <div className={styles.status}>
        <span className={styles.statusMain}>
          <span className={error ? ui.dotErr : sync.isPending ? ui.dotBusy : s?.enabled && s.lastSync ? ui.dotOk : ui.dotWarn} />
          {status.isPending ? (
            <span className={ui.muted}>Checking…</span>
          ) : status.isError ? (
            <span className={ui.error}>{status.error.message}</span>
          ) : !s?.enabled ? (
            <span>Sync is off. Turn it on below to use the guides.</span>
          ) : sync.isPending ? (
            <span>Fetching the guides…</span>
          ) : s.lastSync ? (
            <span title={formatDateTime(s.lastSync)}>Synced {formatAgo(s.lastSync)}</span>
          ) : (
            <span>Not synced yet</span>
          )}
          {s?.commit && (
            <span className={styles.commit} title={`${s.repository} @ ${s.branch}`}>
              {s.commit.slice(0, 7)}
            </span>
          )}
        </span>
        {counts && s?.lastSync && (
          <span className={styles.counts}>
            <span>
              <b>{counts.movieFormats}</b> movie formats
            </span>
            <span>
              <b>{counts.seriesFormats}</b> series formats
            </span>
            <span>
              <b>{counts.movieProfiles + counts.seriesProfiles}</b> templates
            </span>
          </span>
        )}
        <span className={styles.statusActions}>
          {canEdit && (
            <button className={`${ui.button} ${ui.sm}`} type="button" onClick={() => sync.mutate()} disabled={sync.isPending || !s?.enabled}>
              <Icon name="restart" size={14} className={sync.isPending ? styles.spin : undefined} />
              {sync.isPending ? 'Syncing…' : 'Sync now'}
            </button>
          )}
          <button
            className={`${ui.ghost} ${ui.sm}`}
            type="button"
            onClick={() => setSettingsOpen((o) => !o)}
            aria-expanded={settingsOpen}
            aria-controls="trash-settings"
          >
            <Icon name="sliders" size={14} />
            Sync settings
            <Icon name="down" size={13} className={`${styles.chevron} ${settingsOpen ? styles.chevronOpen : ''}`} />
          </button>
        </span>
      </div>
      {error && (
        <p className={`${ui.errBox} ${styles.statusError}`}>
          <Icon name="alert" size={15} />
          {error}
        </p>
      )}

      {settingsOpen && <TrashSettingsRows canEdit={canEdit} />}

      {wizard && <TrashWizard onClose={() => setWizard(false)} />}
      {recyclarr && <RecyclarrDialog onClose={() => setRecyclarr(false)} />}
    </Section>
  )
}

function TrashSettingsRows({ canEdit }: { canEdit: boolean }) {
  const form = useSettingsForm<TrashSettings>({
    queryKey: trashKeys.settings,
    load: trashApi.settings,
    save: trashApi.saveSettings,
    invalidate: [trashKeys.status],
  })
  const v = form.values
  if (form.query.isError) return <p className={ui.errBox}>{form.query.error.message}</p>
  if (!v) return null
  return (
    <div id="trash-settings" className={styles.settings}>
      <fieldset className={styles.settingsFields} disabled={!canEdit}>
        <Row label="Use the TRaSH Guides" help="Download the guides to offer their profiles and custom formats." error={form.error('enabled')}>
          <Toggle checked={v.enabled} onChange={(enabled) => form.set({ enabled })} />
        </Row>
        <Row
          label="Update automatically"
          help="Formats and profiles made from a template follow the guides after each sync."
          error={form.error('autoUpdate')}
        >
          <Toggle checked={v.autoUpdate} onChange={(autoUpdate) => form.set({ autoUpdate })} />
        </Row>
        <Row label="Keep my score changes" help="Scores you changed by hand stay when a profile is updated from the guides." error={form.error('keepScoreOverrides')}>
          <Toggle checked={v.keepScoreOverrides} onChange={(keepScoreOverrides) => form.set({ keepScoreOverrides })} />
        </Row>
        <Row label="Sync every" help="How often StreamZH fetches the guides." error={form.error('syncIntervalHours')}>
          <NumberInput value={v.syncIntervalHours} onChange={(n) => form.set({ syncIntervalHours: n ?? 0 })} unit="hours" min={1} max={720} />
        </Row>
        <Row label="Repository" help="A Git repository with the TRaSH Guides layout, e.g. your own fork." advanced error={form.error('repository')}>
          <input className={ui.input} value={v.repository} onChange={(e) => form.set({ repository: e.target.value })} spellCheck={false} />
        </Row>
        <Row label="Branch" advanced error={form.error('branch')}>
          <input className={`${ui.input} ${styles.branch}`} value={v.branch} onChange={(e) => form.set({ branch: e.target.value })} spellCheck={false} />
        </Row>
      </fieldset>
      <AdvancedHint count={2} />
      {canEdit && (
        <SaveBar dirty={form.dirty} saving={form.saving} saved={form.saved} error={form.formError} onSave={form.save} onReset={form.reset} />
      )}
    </div>
  )
}

/** «Update from TRaSH» on a managed profile: applies its templates again. */
export function UpdateFromTrashDialog({ profile, onClose }: { profile: QualityProfile; onClose: () => void }) {
  const queryClient = useQueryClient()
  const settings = useQuery({ queryKey: trashKeys.settings, queryFn: trashApi.settings, retry: false })
  const run = useMutation({
    mutationFn: () =>
      trashApi.apply({
        profileId: profile.id,
        movieTemplate: profile.trashMovieTemplate ?? undefined,
        seriesTemplate: profile.trashSeriesTemplate ?? undefined,
        applyQualitySizes: false,
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: profileKeys.profiles })
      await queryClient.invalidateQueries({ queryKey: customFormatKeys.all })
    },
  })
  const keep = settings.data?.keepScoreOverrides ?? true

  return (
    <Modal title={`Update ${profile.name}`} onClose={onClose}>
      {run.isSuccess ? (
        <>
          <p className={ui.infoBox}>
            <Icon name="check" size={15} />
            Up to date with the guides. {run.data.created} format{run.data.created === 1 ? '' : 's'} added, {run.data.updated} updated.
          </p>
          <div className={styles.foot}>
            <button className={ui.primary} type="button" onClick={onClose}>
              Done
            </button>
          </div>
        </>
      ) : (
        <>
          <p className={styles.text}>
            Qualities, score limits and format scores follow the current TRaSH template again.{' '}
            {keep ? 'Scores you changed by hand stay.' : 'Scores you changed by hand are replaced (see Sync settings).'}
          </p>
          {run.error && <p className={ui.errBox}>{run.error.message}</p>}
          <div className={styles.foot}>
            <button className={ui.button} type="button" onClick={onClose}>
              Cancel
            </button>
            <button className={ui.primary} type="button" onClick={() => run.mutate()} disabled={run.isPending}>
              <Icon name="restart" size={15} />
              {run.isPending ? 'Updating…' : 'Update'}
            </button>
          </div>
        </>
      )}
    </Modal>
  )
}
