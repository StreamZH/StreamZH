import { KodiMetadataSection } from './KodiMetadataSection'
import { MetadataSection } from './MetadataSection'
import { SettingsPage } from './SettingsKit'

/** Settings → Metadata: TMDB, and the NFO files and artwork StreamZH writes next to the media. */
export default function MetadataPage() {
  return (
    <SettingsPage title="Metadata" lead="Where titles, posters and episode lists come from, and what StreamZH writes next to your media.">
      <MetadataSection />
      <KodiMetadataSection />
    </SettingsPage>
  )
}
