import { ComingSoon } from '../settings/SettingsKit'
import styles from '../settings/SettingsKit.module.css'

/** Placeholder, filled in 0.7. */
export default function FileRenamerPage() {
  return (
    <div className={styles.page}>
      <header className={styles.header}>
        <div>
          <h1 className={styles.title}>File renamer</h1>
          <p className={styles.lead}>Rename many files at once with rules, filters and regular expressions, with a preview before anything changes.</p>
        </div>
      </header>
      <ComingSoon>Being built.</ComingSoon>
    </div>
  )
}
