import type { ReactNode } from 'react'
import Icon, { type IconName } from '../components/Icon'
import ui from '../components/ui.module.css'
import type { StatusIconName } from '../status/icons'
import styles from './Activity.module.css'

/** The CloudZH icon for each of the older status icon names (Empty is also used by the Wanted page). */
const emptyIcons: Record<StatusIconName, IconName> = {
  info: 'info',
  warn: 'alert',
  error: 'alert',
  check: 'check',
  x: 'x',
  up: 'arrowup',
  down: 'down',
  pause: 'pause',
  play: 'play',
  retry: 'restart',
  trash: 'trash',
  upload: 'upload',
  download: 'download',
  disk: 'hdd',
  gauge: 'gauge',
  folder: 'folder',
  server: 'server',
}

/** Empty or error state of a list (CloudZH .empty in a panel). */
export function Empty({ icon, title, children }: { icon: StatusIconName; title: string; children: ReactNode }) {
  return (
    <div className={`${ui.panel} ${styles.emptyPanel}`}>
      <div className={`${ui.empty} ${styles.empty}`}>
        <span className={`${styles.emptyIcon} ${icon === 'error' || icon === 'warn' ? styles.emptyIconErr : ''}`}>
          <Icon name={emptyIcons[icon]} size={20} />
        </span>
        <h3>{title}</h3>
        {children}
      </div>
    </div>
  )
}
