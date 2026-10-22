import { Link } from 'react-router'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import advancedStyles from '../lib/Advanced.module.css'
import { setAdvanced } from '../lib/advancedMode'
import { useShowMigration } from '../migration/hooks'
import { settingsGroups, systemAreas, type SettingsArea } from './areas'
import styles from './SettingsKit.module.css'
import { SettingsPage } from './SettingsKit'

function AreaGrid({ areas }: { areas: SettingsArea[] }) {
  return (
    <div className={styles.areas}>
      {areas.map((area) => (
        <Link key={area.to} className={styles.area} to={area.to}>
          <span className={styles.areaIcon} aria-hidden="true">
            <Icon name={area.icon} size={19} />
          </span>
          <span className={styles.areaBody}>
            <span className={styles.areaTitle}>{area.label}</span>
            <span className={styles.areaText}>{area.description}</span>
          </span>
        </Link>
      ))}
    </div>
  )
}

/** /settings: all areas at a glance, in the groups of the tabs above. Migration only with the Advanced switch on. */
export default function SettingsOverview() {
  const migration = useShowMigration()
  return (
    <SettingsPage title="Settings" lead="Everything StreamZH can be set up with. Most areas work with their defaults; the Advanced switch shows the expert settings.">
      {settingsGroups
        .filter((g) => !g.advanced || migration.show)
        .map((g) => (
          <section key={g.key} aria-labelledby={`settings-group-${g.key}`} className={styles.areaSection}>
            <h2 id={`settings-group-${g.key}`} className={`${ui.eyebrow} ${styles.areaGroup}`}>
              <span className={g.advanced && !migration.unfinished ? advancedStyles.label : undefined}>{g.label}</span>
            </h2>
            <AreaGrid areas={g.areas} />
          </section>
        ))}
      {!migration.show && (
        <p className={advancedStyles.hint}>
          Coming from Radarr, Sonarr, Prowlarr or SABnzbd? The migration is under Advanced.{' '}
          <button type="button" className={advancedStyles.hintButton} onClick={() => setAdvanced(true)}>
            Show it
          </button>
        </p>
      )}
      <section aria-labelledby="settings-group-system" className={styles.areaSection}>
        <h2 id="settings-group-system" className={`${ui.eyebrow} ${styles.areaGroup}`}>
          System
        </h2>
        <AreaGrid areas={systemAreas} />
      </section>
    </SettingsPage>
  )
}
