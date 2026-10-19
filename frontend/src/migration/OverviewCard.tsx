import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { libraryApi, queryKeys } from '../api/library'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import { useIsAdmin } from '../status/useOngoing'
import styles from './Migration.module.css'

/** On the Overview while the library is empty: a way into the migration wizard for people coming from the *arr apps. */
export default function MigrationCard() {
  const admin = useIsAdmin()
  const stats = useQuery({ queryKey: queryKeys.stats, queryFn: libraryApi.stats })
  if (!admin || !stats.data || stats.data.movies + stats.data.series > 0) return null
  return (
    <section className={`${ui.panel} ${styles.card}`} aria-label="Migration">
      <span className={styles.cardIcon} aria-hidden="true">
        <Icon name="mountin" size={20} />
      </span>
      <div className={styles.cardText}>
        <h2>Coming from Radarr, Sonarr or SABnzbd?</h2>
        <p>Take over your settings and your library with its files in a few steps. The old apps keep running until you switch over.</p>
      </div>
      <Link to="/settings/migration" className={ui.primary}>
        Start migration
        <Icon name="right" size={15} />
      </Link>
    </section>
  )
}
