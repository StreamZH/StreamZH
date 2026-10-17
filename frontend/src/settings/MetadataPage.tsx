import { MetadataSection } from './MetadataSection'
import { SettingsPage } from './SettingsKit'

/** Settings → Metadata: TMDB; NFO files and artwork follow in 0.6. */
export default function MetadataPage() {
  return (
    <SettingsPage title="Metadata" lead="Where titles, posters and episode lists come from, and what StreamZH writes next to your media.">
      <MetadataSection />
    </SettingsPage>
  )
}
