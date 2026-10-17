import { ComingSoon } from '../settings/SettingsKit'
import styles from '../settings/SettingsKit.module.css'

/** Placeholder, filled in 0.7. */
export default function CalendarPage() {
  return (
    <div className={styles.page}>
      <header className={styles.header}>
        <div>
          <h1 className={styles.title}>Calendar</h1>
          <p className={styles.lead}>Upcoming movies and episodes, and what was released recently.</p>
        </div>
      </header>
      <ComingSoon>Being built.</ComingSoon>
    </div>
  )
}
