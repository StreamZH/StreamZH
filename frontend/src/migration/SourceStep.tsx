import type { MigrationRun, MigrationSource } from '../api/migration'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import { formatDate } from '../lib/format'
import styles from './Migration.module.css'
import { nextSource, sources, utc } from './sources'

/** Step 1: the four old apps in the recommended order, with what each brings and a check mark once taken over. */
export default function SourceStep({ runs, onPick }: { runs: MigrationRun[] | undefined; onPick: (source: MigrationSource) => void }) {
  const next = nextSource(runs)
  const last = (key: string) => (runs ?? []).find((r) => r.source === key && r.status === 'done' && !r.dryRun)
  return (
    <div className={styles.stepBody}>
      <p className={styles.stepLead}>
        Pick the app to take over. Start with Radarr, then Sonarr, so profiles and folders are in place before the downloads move.
      </p>
      <div className={styles.sources}>
        {sources.map((s, index) => {
          const done = last(s.key)
          const recommended = next?.key === s.key
          return (
            <button
              key={s.key}
              type="button"
              className={`${styles.source} ${recommended ? styles.sourceNext : ''} ${done ? styles.sourceDone : ''}`}
              onClick={() => onPick(s.key)}
            >
              <span className={styles.sourceTop}>
                <span className={styles.sourceIcon} aria-hidden="true">
                  <Icon name={s.icon} size={18} />
                </span>
                <span className={styles.sourceOrder}>{s.optional ? 'Optional' : `Step ${index + 1 - (index > 2 ? 1 : 0)}`}</span>
                {done ? (
                  <span className={`${ui.pill} ${ui.pillOk} ${styles.sourceState}`} title={`Taken over ${formatDate(utc(done.started))}`}>
                    <Icon name="check" size={13} />
                    Done
                  </span>
                ) : (
                  recommended && <span className={`${ui.pill} ${ui.pillBusy} ${styles.sourceState}`}>Next</span>
                )}
              </span>
              <span className={styles.sourceName}>{s.label}</span>
              <span className={styles.sourceText}>{s.brings}</span>
              {s.optionalHint && <span className={styles.sourceHint}>{s.optionalHint}</span>}
            </button>
          )
        })}
      </div>
    </div>
  )
}
