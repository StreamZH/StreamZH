import { RootFoldersSection } from './RootFoldersSection'
import { SettingsPage } from './SettingsKit'

/** Settings → Media Management: root folders; naming, importing, file handling and permissions follow in 0.6. */
export default function MediaManagementPage() {
  return (
    <SettingsPage title="Media Management" lead="Where your movies and series live, how files are named and how downloads are imported.">
      <RootFoldersSection />
    </SettingsPage>
  )
}
