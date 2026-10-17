import type { ReactNode } from 'react'
import styles from '../settings/SettingsKit.module.css'

/** Page frame for System → Status, Tasks, Backup, Logs. */
export function SystemPage({ title, lead, actions, children }: { title: string; lead?: ReactNode; actions?: ReactNode; children: ReactNode }) {
  return (
    <div className={styles.page}>
      <header className={styles.header}>
        <div>
          <div className={styles.eyebrow}>System</div>
          <h1 className={styles.title}>{title}</h1>
          {lead && <p className={styles.lead}>{lead}</p>}
        </div>
        {actions && <div className={styles.headerActions}>{actions}</div>}
      </header>
      {children}
    </div>
  )
}
