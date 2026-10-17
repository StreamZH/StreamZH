import { DownloadsSection } from './DownloadsSection'
import { ToolsSection } from '../pages/ToolsSettings'
import { SettingsPage } from './SettingsKit'

/** Settings → Downloads: download client and engine, tools. */
export default function DownloadsPage() {
  return (
    <SettingsPage title="Downloads" lead="How grabbed releases are downloaded, repaired and unpacked.">
      <DownloadsSection />
      <ToolsSection />
    </SettingsPage>
  )
}
