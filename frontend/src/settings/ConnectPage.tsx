import { ConnectionsSection } from '../connect/ConnectionsSection'
import { SettingsPage } from './SettingsKit'

/** Settings → Connect: media servers (Plex, Jellyfin, Emby, Kodi) and notifications, like Radarr's Connect page. */
export default function ConnectPage() {
  return (
    <SettingsPage
      title="Connect"
      lead="Tell Plex, Jellyfin, Emby or Kodi about new files, and get notified on Discord, Telegram, by email and more. Messages are sent in the background and retried when a service is down."
    >
      <ConnectionsSection />
    </SettingsPage>
  )
}
