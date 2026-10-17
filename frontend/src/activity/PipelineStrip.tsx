import StatusIcon from '../status/icons'
import styles from './Activity.module.css'
import { stages, stageStates, stateText, type PipelineItem } from './pipeline'

export default function PipelineStrip({ item }: { item: PipelineItem }) {
  const states = stageStates(item)
  const current = stages.find((s) => states[s.key] !== 'done' && states[s.key] !== 'todo') ?? stages[stages.length - 1]

  return (
    <div className={styles.pipelineWrap}>
      <ol className={styles.pipeline} aria-label="Pipeline">
        {stages.map((stage, i) => {
          const state = states[stage.key]
          return [
            i > 0 && <li key={`${stage.key}-line`} aria-hidden="true" className={`${styles.line} ${state !== 'todo' ? styles.lineOn : ''}`} />,
            <li
              key={stage.key}
              className={`${styles.stage} ${styles[`stage_${state}`]}`}
              aria-current={state === 'current' || state === 'paused' || state === 'waiting' || state === 'failed' ? 'step' : undefined}
              aria-label={`${stage.label}: ${stateText[state]}`}
            >
              <span className={styles.stageDot} aria-hidden="true">
                {state === 'done' ? (
                  <StatusIcon name="check" size={11} />
                ) : state === 'failed' ? (
                  <StatusIcon name="x" size={11} />
                ) : state === 'paused' ? (
                  <StatusIcon name="pause" size={10} />
                ) : null}
              </span>
              <span className={styles.stageLabel}>{stage.label}</span>
            </li>,
          ]
        })}
      </ol>
      <p className={styles.pipelineCaption} aria-hidden="true">
        Step {stages.indexOf(current) + 1} of {stages.length} · <b>{current.label}</b>
        {states[current.key] !== 'current' && states[current.key] !== 'done' && ` · ${stateText[states[current.key]]}`}
      </p>
    </div>
  )
}
