import { Link } from 'react-router'
import { mediaInfoApi, mediaInfoKeys, type MediaInfoSettings } from '../api/mediaInfo'
import ui from '../components/ui.module.css'
import { NumberInput, Row, SaveBar, Section, Toggle } from './SettingsKit'
import { useSettingsForm } from './useSettingsForm'

/**
 * Settings → Media Management → Media info: analyse video files with ffprobe (Radarr's "Analyse video files") and how
 * often "Refresh media info" checks the library for new and changed files.
 */
export function MediaInfoSection() {
  const form = useSettingsForm<MediaInfoSettings>({
    queryKey: mediaInfoKeys.settings,
    load: mediaInfoApi.settings,
    save: mediaInfoApi.saveSettings,
  })
  const values = form.values
  if (!values) return null

  return (
    <Section
      id="media-info"
      title="Media info"
      description="Codecs, resolution, HDR and audio languages read from the files with ffprobe. Used in file names, for files whose name has no language or resolution, and on the detail pages."
      hiddenCount={1}
      footer={
        <SaveBar dirty={form.dirty} saving={form.saving} saved={form.saved} error={form.formError} onSave={form.save} onReset={form.reset} />
      }
    >
      {!values.ffprobeFound && (
        <p className={ui.infoBox}>
          ffprobe was not found. Install it in <Link to="/settings/downloads#tools">Settings → Downloads → Tools</Link>.
        </p>
      )}
      <Row label="Analyse video files" help="Read new and changed files at import and after library scans.">
        <Toggle checked={values.analyze} onChange={(analyze) => form.set({ analyze })} />
      </Row>
      <Row
        label="Refresh media info"
        htmlFor="mi-refresh"
        advanced
        error={form.error('refreshIntervalDays')}
        help="Checks the whole library for files that are new, changed or analysed by an older StreamZH. 0 = off."
      >
        <NumberInput id="mi-refresh" value={values.refreshIntervalDays} onChange={(v) => form.set({ refreshIntervalDays: v ?? 0 })} unit="days" min={0} max={365} />
      </Row>
    </Section>
  )
}
