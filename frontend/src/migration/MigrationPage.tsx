import { useQuery } from '@tanstack/react-query'
import { isLive, migrationApi, migrationKeys } from '../api/migration'
import Icon from '../components/Icon'
import { Skeleton } from '../components/Loader'
import ui from '../components/ui.module.css'
import { SettingsPage } from '../settings/SettingsKit'
import styles from './Migration.module.css'
import RunHistory from './RunHistory'
import { SideBySideSection } from './SideBySide'
import Wizard from './Wizard'

/** Settings → Migration: take over Radarr, Sonarr, Prowlarr and SABnzbd; side by side; past runs. */
export default function MigrationPage() {
  const runs = useQuery({
    queryKey: migrationKeys.runs,
    queryFn: migrationApi.runs,
    refetchInterval: (q) => (q.state.data?.some(isLive) ? 3000 : false),
  })
  const running = runs.data?.find(isLive)

  return (
    <SettingsPage
      title="Migration"
      lead="Take over Radarr, Sonarr, Prowlarr and SABnzbd: settings, indexers and your library with its files. The old apps are only read, never changed."
    >
      {runs.isError && (
        <p className={ui.errBox} role="alert">
          <Icon name="alert" size={15} />
          {runs.error.message}
        </p>
      )}
      {runs.isPending ? (
        <section className={`${ui.panel} ${styles.wizard}`}>
          <Skeleton rows={4} />
        </section>
      ) : (
        <Wizard runs={runs.data} resume={running?.id ?? null} />
      )}
      <SideBySideSection />
      {runs.data && <RunHistory runs={runs.data} />}
    </SettingsPage>
  )
}
