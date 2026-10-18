import type { ReactNode } from 'react'
import PageHeader from '../components/PageHeader'
import styles from '../settings/SettingsKit.module.css'

/** Page frame for System → Status, Tasks, Backup, Logs: the CloudZH page head with the eyebrow «System». */
export function SystemPage({
  title,
  lead,
  actions,
  wide = false,
  children,
}: {
  title: string
  lead?: ReactNode
  actions?: ReactNode
  /** Use the whole width (logs). */
  wide?: boolean
  children: ReactNode
}) {
  return (
    <div className={`${styles.page} ${wide ? styles.pageWide : ''}`}>
      <PageHeader eyebrow="System" title={title} lead={lead} actions={actions} />
      {children}
    </div>
  )
}
