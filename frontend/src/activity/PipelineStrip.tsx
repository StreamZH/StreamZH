import Icon from '../components/Icon'
import styles from './Activity.module.css'
import { stages, stageStates, stateText, type PipelineItem } from './pipeline'

/**
 * Where a download stands in the flow Grab → Download → Verify → Repair → Extract → Import, as a calm one-line stepper.
 * Kept next to the status pill because it shows what still comes (repair, unpack, import), which the pill does not.
 */
export default function PipelineStrip({ item }: { item: PipelineItem }) {
  const states = stageStates(item)
  const current = stages.find((s) => states[s.key] !== 'done' && states[s.key] !== 'todo') ?? stages[stages.length - 1]

  return (
    <ol className={styles.steps} aria-label={`Pipeline: step ${stages.indexOf(current) + 1} of ${stages.length}, ${current.label}`}>
      {stages.map((stage) => {
        const state = states[stage.key]
        return (
          <li
            key={stage.key}
            className={styles[`step_${state}`]}
            aria-current={state === 'current' || state === 'paused' || state === 'waiting' || state === 'failed' ? 'step' : undefined}
            aria-label={`${stage.label}: ${stateText[state]}`}
            title={`${stage.label}: ${stateText[state]}`}
          >
            <span className={styles.stepDot} aria-hidden="true">
              {state === 'done' ? <Icon name="check" size={9} /> : state === 'failed' ? <Icon name="x" size={9} /> : null}
            </span>
            <span className={styles.stepLabel}>{stage.label}</span>
          </li>
        )
      })}
    </ol>
  )
}
