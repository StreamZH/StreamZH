import type { ReactNode } from 'react'
import styles from './PageHeader.module.css'

/** The head of every page (CloudZH .phead): serif title, an optional line below and actions on the right. */
export default function PageHeader({
  title,
  lead,
  eyebrow,
  actions,
}: {
  title: ReactNode
  lead?: ReactNode
  eyebrow?: ReactNode
  actions?: ReactNode
}) {
  return (
    <header className={styles.head}>
      <div className={styles.text}>
        {eyebrow && <div className={styles.eyebrow}>{eyebrow}</div>}
        <h1 className={styles.title}>{title}</h1>
        {lead && <p className={styles.lead}>{lead}</p>}
      </div>
      {actions && <div className={styles.actions}>{actions}</div>}
    </header>
  )
}
