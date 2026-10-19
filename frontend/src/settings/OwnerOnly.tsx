import type { ReactNode } from 'react'
import Icon from '../components/Icon'
import styles from './OwnerOnly.module.css'

/** Shown instead of the save bar of a section only the owner may change. */
export function OwnerOnlyNote({ children }: { children?: ReactNode }) {
  return (
    <p className={styles.note}>
      <Icon name="lock" size={14} />
      {children ?? 'Only the owner can change these settings.'}
    </p>
  )
}

/** Disables every field inside while `locked` (the values stay visible). */
export function ReadOnly({ locked, children }: { locked: boolean; children: ReactNode }) {
  if (!locked) return <>{children}</>
  return (
    <fieldset className={styles.fieldset} disabled>
      {children}
    </fieldset>
  )
}
