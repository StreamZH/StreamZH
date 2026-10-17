import { Link } from 'react-router'
import { settingsAreas } from './areas'
import styles from './SettingsKit.module.css'
import { SettingsPage } from './SettingsKit'

/** /settings: all areas at a glance (the sidebar lists them too). */
export default function SettingsOverview() {
  return (
    <SettingsPage title="Settings" lead="Everything StreamZH can be set up with. Most areas work with their defaults; the Advanced switch shows the expert settings.">
      <div className={styles.areas}>
        {settingsAreas.map((area) => (
          <Link key={area.to} className={styles.area} to={area.to}>
            <span className={styles.areaTitle}>{area.label}</span>
            <span className={styles.areaText}>{area.description}</span>
          </Link>
        ))}
      </div>
    </SettingsPage>
  )
}
