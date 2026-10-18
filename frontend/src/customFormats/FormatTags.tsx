import { formatFormatScore, type MatchedFormat } from '../api/customFormats'
import styles from './FormatTags.module.css'

/** Custom format score as a small pill: green above zero, red below, neutral at zero. */
export function ScorePill({ score, title, label }: { score: number; title?: string; label?: string }) {
  const tone = score > 0 ? styles.plus : score < 0 ? styles.minus : styles.zero
  return (
    <span className={`${styles.score} ${tone}`} title={title ?? 'Custom format score'}>
      {label && <span className={styles.scoreLabel}>{label}</span>}
      {formatFormatScore(score)}
    </span>
  )
}

/** A line below a release title: the format score (when not zero) and the matched formats. Nothing when neither. */
export function FormatLine({ formats, score, max = 5 }: { formats: MatchedFormat[] | undefined; score: number | undefined; max?: number }) {
  if ((formats?.length ?? 0) === 0 && !score) return null
  return (
    <span className={styles.line}>
      {!!score && <ScorePill score={score} />}
      <FormatTags formats={formats} max={max} />
    </span>
  )
}

/** The custom formats a release matched, as small tags; more than `max` collapse into "+n". */
export function FormatTags({ formats, max = 4 }: { formats: MatchedFormat[] | undefined; max?: number }) {
  if (!formats || formats.length === 0) return null
  const shown = formats.slice(0, max)
  const rest = formats.slice(max)
  return (
    <span className={styles.tags} aria-label={`Custom formats: ${formats.map((f) => f.name).join(', ')}`}>
      {shown.map((f) => (
        <span key={f.id} className={styles.tag}>
          {f.name}
        </span>
      ))}
      {rest.length > 0 && (
        <span className={`${styles.tag} ${styles.more}`} title={rest.map((f) => f.name).join(', ')}>
          +{rest.length}
        </span>
      )}
    </span>
  )
}
