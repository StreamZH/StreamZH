import { useAuth } from '../auth/auth'
import { SizeLimits } from '../pages/Profiles'
import { SettingsPage } from './SettingsKit'

/** Settings → Quality: size limits per quality (like Radarr's Quality page). */
export default function QualityPage() {
  const { user } = useAuth()
  const canEdit = user?.role === 'Owner' || user?.role === 'Admin'
  return (
    <SettingsPage title="Quality" lead="Minimum, preferred and maximum size per quality, in MB per minute of runtime.">
      <SizeLimits canEdit={canEdit} defaultOpen />
    </SettingsPage>
  )
}
