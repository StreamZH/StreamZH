import type { ReactNode } from 'react'
import styles from './Advanced.module.css'
import { setAdvanced, useAdvanced } from './advancedMode'

/** The switch, for page headers and dialog headers. */
export function AdvancedToggle() {
  const advanced = useAdvanced()
  return (
    <label className={`${styles.toggle} ${advanced ? styles.on : ''}`} title="Show expert settings">
      <input type="checkbox" role="switch" checked={advanced} onChange={(e) => setAdvanced(e.target.checked)} />
      <span className={styles.track} aria-hidden="true" />
      <span>Advanced</span>
    </label>
  )
}

/** Renders its children only in advanced mode. */
export function Advanced({ children }: { children: ReactNode }) {
  return useAdvanced() ? <>{children}</> : null
}

/** Line under a form while expert settings are hidden: "4 expert settings hidden. Show them". */
export function AdvancedHint({ count }: { count: number }) {
  const advanced = useAdvanced()
  if (advanced || count <= 0) return null
  return (
    <p className={styles.hint}>
      {count} expert setting{count === 1 ? '' : 's'} hidden.{' '}
      <button type="button" className={styles.hintButton} onClick={() => setAdvanced(true)}>
        Show them
      </button>
    </p>
  )
}
