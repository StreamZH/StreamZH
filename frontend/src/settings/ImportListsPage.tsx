import { ExclusionsSection } from '../importLists/ExclusionsSection'
import { ListsSection } from '../importLists/ListsSection'
import { OptionsSection } from '../importLists/OptionsSection'
import { SettingsPage } from './SettingsKit'

/** Settings → Import lists (Radarr's "Lists", Sonarr's "Import Lists"): lists, options and exclusions. */
export default function ImportListsPage() {
  return (
    <SettingsPage
      title="Import lists"
      lead="Add movies and series automatically from TMDB, your Plex watchlist, Trakt and more. Every list has its own root folder, quality profile and monitoring."
    >
      <ListsSection />
      <OptionsSection />
      <ExclusionsSection />
    </SettingsPage>
  )
}
