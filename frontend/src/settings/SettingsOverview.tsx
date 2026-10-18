import { Link } from 'react-router'
import Icon, { type IconName } from '../components/Icon'
import { groups } from '../components/shell/nav'
import ui from '../components/ui.module.css'
import { settingsAreas, systemAreas, type SettingsArea } from './areas'
import styles from './SettingsKit.module.css'
import { SettingsPage } from './SettingsKit'

// The same icons as the sidebar (components/shell/nav.ts).
const navIcons = new Map<string, IconName>(groups.flatMap((g) => g.pages.map((p) => [p.to, p.icon] as const)))

function AreaGrid({ areas }: { areas: SettingsArea[] }) {
  return (
    <div className={styles.areas}>
      {areas.map((area) => (
        <Link key={area.to} className={styles.area} to={area.to}>
          <span className={styles.areaIcon} aria-hidden="true">
            <Icon name={navIcons.get(area.to) ?? 'sliders'} size={19} />
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

/** /settings: all areas at a glance (the sidebar lists them too). */
export default function SettingsOverview() {
  return (
    <SettingsPage title="Settings" lead="Everything StreamZH can be set up with. Most areas work with their defaults; the Advanced switch shows the expert settings.">
      <AreaGrid areas={settingsAreas} />
      <h2 className={`${ui.eyebrow} ${styles.areaGroup}`}>System</h2>
      <AreaGrid areas={systemAreas} />
    </SettingsPage>
  )
}
