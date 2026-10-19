import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { libraryApi, queryKeys } from '../api/library'
import { useProfileChoices } from '../api/profiles'
import { requestKeys, requestsApi, type RequestSettings, type SeriesMonitor } from '../api/requests'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import { useAdvanced } from '../lib/advancedMode'
import { useAutomation } from '../migration/hooks'
import { NumberInput, Row, SaveBar, Section, Select, SettingsPage, Toggle } from '../settings/SettingsKit'
import { useSettingsForm } from '../settings/useSettingsForm'
import styles from './RequestSettings.module.css'

const monitorOptions: { value: SeriesMonitor; label: string }[] = [
  { value: 'requested', label: 'Only the requested seasons' },
  { value: 'all', label: 'All seasons' },
  { value: 'future', label: 'Requested and future seasons' },
]

/** Settings → Requests: who may request, what gets approved by itself, limits, and how approved titles are added. */
export default function RequestSettingsPage() {
  const advanced = useAdvanced()
  const form = useSettingsForm<RequestSettings>({
    queryKey: requestKeys.settings,
    load: requestsApi.settings,
    save: (draft) => requestsApi.saveSettings(draft),
  })
  const { profiles } = useProfileChoices()
  const folders = useQuery({ queryKey: queryKeys.rootFolders, queryFn: libraryApi.rootFolders })
  const automation = useAutomation()
  const v = form.values

  const profileOptions = [{ value: '', label: 'Default profile' }, ...profiles.map((p) => ({ value: String(p.id), label: p.name }))]
  const folderOptions = (type: 'Movie' | 'Series') => [
    { value: '', label: 'First root folder' },
    ...(folders.data ?? []).filter((f) => f.mediaType === type).map((f) => ({ value: String(f.id), label: f.path })),
  ]
  const id = (value: string) => (value === '' ? null : Number(value))
  const saveBar = <SaveBar dirty={form.dirty} saving={form.saving} saved={form.saved} error={form.formError} onSave={form.save} onReset={form.reset} />

  return (
    <SettingsPage
      title="Requests"
      lead="Let friends and requesters ask for movies and series; you approve them, or StreamZH does it by itself."
      actions={
        <>
          <Link to="/settings/users" className={`${ui.button} ${ui.sm}`}>
            <Icon name="users" size={15} />
            Users
          </Link>
          <Link to="/settings/discord" className={`${ui.button} ${ui.sm}`}>
            <Icon name="chat" size={15} />
            Discord
          </Link>
        </>
      }
    >
      {form.query.isError && <p className={ui.error}>{form.query.error.message}</p>}
      {v && (
        <>
          <Section id="requests" title="Approval" footer={saveBar} hiddenCount={advanced ? 0 : 2}>
            <Row label="Requests" help="Off: Discover still works, but nobody can request; open requests stay.">
              <Toggle checked={v.enabled} onChange={(enabled) => form.set({ enabled })} on="On" off="Off" />
            </Row>
            <Row label="Approve friends automatically" help="Requests from friends go straight to the library. Per user in Settings → Users.">
              <Toggle checked={v.autoApproveFriends} onChange={(autoApproveFriends) => form.set({ autoApproveFriends })} />
            </Row>
            <Row label="Approve requesters automatically" help="Off: an admin approves every request from a requester.">
              <Toggle checked={v.autoApproveRequesters} onChange={(autoApproveRequesters) => form.set({ autoApproveRequesters })} />
            </Row>
            <Row label="Search on approve" advanced error={form.error('searchOnApprove')} help="Look for an approved title right away. Off: it waits for RSS and the automatic search.">
              <Toggle checked={v.searchOnApprove} onChange={(searchOnApprove) => form.set({ searchOnApprove })} />
            </Row>
            {automation.data?.hold && (advanced || v.searchOnApprove) && (
              <div className={`${ui.infoBox} ${styles.hold}`}>
                <Icon name="pause" size={15} />
                <span>Side by side is on: approved titles are added and monitored, but StreamZH grabs nothing until you switch over.</span>
              </div>
            )}
            <Row label="Notify when available" advanced error={form.error('notifyOnAvailable')} help="Tell the requester (Discord, notifications) once the title can be watched.">
              <Toggle checked={v.notifyOnAvailable} onChange={(notifyOnAvailable) => form.set({ notifyOnAvailable })} />
            </Row>
          </Section>

          <Section id="limits" title="Limits" description="Default for every user; each user can get their own in Settings → Users. Empty: no limit. Declined requests do not count." footer={saveBar}>
            <Row label="Movies" htmlFor="defaultMovieLimit" error={form.error('defaultMovieLimit')}>
              <NumberInput id="defaultMovieLimit" value={v.defaultMovieLimit} onChange={(defaultMovieLimit) => form.set({ defaultMovieLimit })} min={0} unit="per period" placeholder="No limit" />
            </Row>
            <Row label="Series" htmlFor="defaultSeriesLimit" error={form.error('defaultSeriesLimit')}>
              <NumberInput id="defaultSeriesLimit" value={v.defaultSeriesLimit} onChange={(defaultSeriesLimit) => form.set({ defaultSeriesLimit })} min={0} unit="per period" placeholder="No limit" />
            </Row>
            <Row label="Period" htmlFor="limitDays" error={form.error('limitDays')} help="The limits count the requests of the last days.">
              <NumberInput id="limitDays" value={v.limitDays} onChange={(limitDays) => form.set({ limitDays })} min={1} max={365} unit="days" />
            </Row>
          </Section>

          <Section
            id="adding"
            title="Adding approved titles"
            description="Where approved requests go and what gets monitored. An admin can pick something else when approving."
            advanced
            footer={saveBar}
          >
            <Row label="Movie profile" htmlFor="defaultMovieProfileId" error={form.error('defaultMovieProfileId')}>
              <Select id="defaultMovieProfileId" value={v.defaultMovieProfileId == null ? '' : String(v.defaultMovieProfileId)} onChange={(x) => form.set({ defaultMovieProfileId: id(x) })} options={profileOptions} />
            </Row>
            <Row label="Movie root folder" htmlFor="defaultMovieRootFolderId" error={form.error('defaultMovieRootFolderId')}>
              <Select id="defaultMovieRootFolderId" value={v.defaultMovieRootFolderId == null ? '' : String(v.defaultMovieRootFolderId)} onChange={(x) => form.set({ defaultMovieRootFolderId: id(x) })} options={folderOptions('Movie')} />
            </Row>
            <Row label="Series profile" htmlFor="defaultSeriesProfileId" error={form.error('defaultSeriesProfileId')}>
              <Select id="defaultSeriesProfileId" value={v.defaultSeriesProfileId == null ? '' : String(v.defaultSeriesProfileId)} onChange={(x) => form.set({ defaultSeriesProfileId: id(x) })} options={profileOptions} />
            </Row>
            <Row label="Series root folder" htmlFor="defaultSeriesRootFolderId" error={form.error('defaultSeriesRootFolderId')}>
              <Select id="defaultSeriesRootFolderId" value={v.defaultSeriesRootFolderId == null ? '' : String(v.defaultSeriesRootFolderId)} onChange={(x) => form.set({ defaultSeriesRootFolderId: id(x) })} options={folderOptions('Series')} />
            </Row>
            <Row label="Monitor" htmlFor="seriesMonitor" error={form.error('seriesMonitor')} help="Which seasons of a requested series StreamZH monitors.">
              <Select id="seriesMonitor" value={v.seriesMonitor} onChange={(seriesMonitor) => form.set({ seriesMonitor })} options={monitorOptions} />
            </Row>
          </Section>
        </>
      )}
    </SettingsPage>
  )
}
