import type { ReactNode } from 'react'
import { Link } from 'react-router'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import { nextStep, stepTitles, useDismissGuide, useSetupGuide } from './useSetupGuide'
import styles from './Welcome.module.css'

/**
 * On the Overview (owners and admins): "Setup: 3 of 5 done" with the next step, until the checklist is complete or
 * hidden. Otherwise shows <paramref name="fallback"/> (the migration card for an empty library).
 */
export default function SetupCard({ fallback }: { fallback?: ReactNode }) {
  const guide = useSetupGuide()
  const dismiss = useDismissGuide()
  const g = guide.data
  if (!g || g.dismissed || g.complete) return <>{fallback}</>

  const next = nextStep(g)
  return (
    <section className={`${ui.panel} ${styles.card}`} aria-label="Setup">
      <span className={styles.extraIcon} aria-hidden="true">
        <Icon name="sparkle" size={18} />
      </span>
      <div className={styles.cardText}>
        <b>
          Setup: {g.done} of {g.total} done
        </b>
        <span>{next ? `Next: ${stepTitles[next]}` : 'A few steps until StreamZH can download for you'}</span>
      </div>
      <div className={styles.cardSide}>
        <Link className={`${ui.primary} ${ui.sm}`} to="/get-started">
          Continue
          <Icon name="right" size={14} />
        </Link>
        <button
          className={`${ui.ghost} ${ui.sm} ${ui.icon}`}
          type="button"
          onClick={() => dismiss.mutate(true)}
          disabled={dismiss.isPending}
          aria-label="Hide the setup guide"
          title="Hide the setup guide (Get started stays in the search, Ctrl K)"
        >
          <Icon name="x" size={15} />
        </button>
      </div>
      <div
        className={`${styles.bar} ${styles.cardBar}`}
        role="progressbar"
        aria-label="Setup progress"
        aria-valuemin={0}
        aria-valuemax={g.total}
        aria-valuenow={g.done}
      >
        <i style={{ width: `${g.total > 0 ? (g.done / g.total) * 100 : 0}%` }} />
      </div>
    </section>
  )
}
