import { ComingSoon } from '../settings/SettingsKit'
import styles from '../settings/SettingsKit.module.css'

/** Placeholder, filled in 0.7. */
export default function WantedPage() {
  return (
    <div className={styles.page}>
      <header className={styles.header}>
        <div>
          <h1 className={styles.title}>Wanted</h1>
          <p className={styles.lead}>Monitored movies and episodes without a file, or below the quality cutoff.</p>
        </div>
      </header>
      <ComingSoon>Being built.</ComingSoon>
    </div>
  )
}
