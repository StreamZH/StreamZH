import { useState, type KeyboardEvent } from 'react'
import { checkTerm, formatScore, isRegexTerm, type PreferredTerm } from '../api/indexerOptions'
import ui from '../components/ui.module.css'
import styles from './IndexerOptions.module.css'

/**
 * Terms as chips (like Radarr's tag inputs): type a word or /regex/ and press Enter. A pasted list with one term per line
 * becomes several chips. Invalid regexes are marked and explained under the field.
 */
export function TermInput({
  id,
  value,
  onChange,
  placeholder = 'Type a term or /regex/ and press Enter',
  label,
}: {
  id?: string
  value: string[]
  onChange: (value: string[]) => void
  placeholder?: string
  label: string
}) {
  const [text, setText] = useState('')
  const draftError = text.trim() ? checkTerm(text) : null

  const add = (raw: string) => {
    const terms = raw
      .split(/\r?\n/)
      .map((t) => t.trim())
      .filter((t) => t && !value.includes(t))
    if (terms.length > 0) onChange([...value, ...terms])
    setText('')
  }

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') {
      e.preventDefault()
      if (text.trim()) add(text)
    } else if (e.key === 'Backspace' && !text && value.length > 0) {
      onChange(value.slice(0, -1))
    }
  }

  const problems = value.map(checkTerm).filter((p): p is string => p !== null)

  return (
    <div className={styles.termField}>
      <div className={`${styles.chips} ${problems.length > 0 || draftError ? styles.chipsInvalid : ''}`}>
        {value.map((term) => {
          const problem = checkTerm(term)
          return (
            <span key={term} className={`${styles.chip} ${isRegexTerm(term) ? styles.regex : ''} ${problem ? styles.chipInvalid : ''}`} title={problem ?? undefined}>
              <span className={styles.chipText}>{term}</span>
              <button type="button" className={styles.chipRemove} onClick={() => onChange(value.filter((t) => t !== term))} aria-label={`Remove ${term}`}>
                ×
              </button>
            </span>
          )
        })}
        <input
          id={id}
          className={styles.chipInput}
          value={text}
          placeholder={value.length === 0 ? placeholder : ''}
          aria-label={label}
          spellCheck={false}
          onChange={(e) => {
            const next = e.target.value
            if (next.includes('\n')) add(next)
            else setText(next)
          }}
          onPaste={(e) => {
            const pasted = e.clipboardData.getData('text')
            if (pasted.includes('\n')) {
              e.preventDefault()
              add(text + pasted)
            }
          }}
          onKeyDown={onKeyDown}
          onBlur={() => {
            if (text.trim() && !draftError) add(text)
          }}
        />
      </div>
      {draftError && <small className={styles.termError}>{draftError}</small>}
      {problems.map((p) => (
        <small key={p} className={styles.termError}>
          {p}
        </small>
      ))}
    </div>
  )
}

/** Preferred terms with a score each; positive scores rank releases up, negative ones down. */
export function PreferredInput({ value, onChange }: { value: PreferredTerm[]; onChange: (value: PreferredTerm[]) => void }) {
  const [term, setTerm] = useState('')
  const [score, setScore] = useState('10')
  const scoreNumber = Number(score)
  const scoreValid = Number.isInteger(scoreNumber) && scoreNumber !== 0 && Math.abs(scoreNumber) <= 10000
  const termError = term.trim() ? checkTerm(term) : null
  const duplicate = value.some((p) => p.term === term.trim())
  const canAdd = !!term.trim() && !termError && scoreValid && !duplicate

  const add = () => {
    if (!canAdd) return
    onChange([...value, { term: term.trim(), score: scoreNumber }])
    setTerm('')
  }

  const update = (index: number, change: Partial<PreferredTerm>) => onChange(value.map((p, i) => (i === index ? { ...p, ...change } : p)))

  return (
    <div className={styles.preferred}>
      {value.length > 0 && (
        <ul className={styles.preferredList}>
          {value.map((p, index) => {
            const problem = checkTerm(p.term)
            return (
              <li key={p.term} className={styles.preferredRow}>
                <span className={`${styles.chip} ${isRegexTerm(p.term) ? styles.regex : ''} ${problem ? styles.chipInvalid : ''}`} title={problem ?? undefined}>
                  <span className={styles.chipText}>{p.term}</span>
                </span>
                <input
                  className={`${ui.input} ${styles.score} ${p.score > 0 ? styles.plus : styles.minus}`}
                  type="number"
                  step={1}
                  value={p.score}
                  aria-label={`Score of ${p.term}`}
                  onChange={(e) => update(index, { score: e.target.value === '' ? 0 : Math.trunc(e.target.valueAsNumber) })}
                />
                <button
                  type="button"
                  className={ui.button}
                  onClick={() => onChange(value.filter((_, i) => i !== index))}
                  aria-label={`Remove ${p.term}`}
                  title="Remove"
                >
                  ×
                </button>
              </li>
            )
          })}
        </ul>
      )}
      <div className={styles.preferredRow}>
        <input
          className={`${ui.input} ${ui.mono} ${styles.preferredTerm}`}
          value={term}
          placeholder="Term or /regex/"
          aria-label="New preferred term"
          spellCheck={false}
          onChange={(e) => setTerm(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault()
              add()
            }
          }}
        />
        <input
          className={`${ui.input} ${styles.score}`}
          type="number"
          step={1}
          value={score}
          aria-label="Score of the new term"
          onChange={(e) => setScore(e.target.value)}
        />
        <button type="button" className={ui.button} onClick={add} disabled={!canAdd}>
          Add {scoreValid ? formatScore(scoreNumber) : ''}
        </button>
      </div>
      {termError && <small className={styles.termError}>{termError}</small>}
      {!scoreValid && <small className={styles.termError}>Use a whole number from -10000 to 10000, not 0.</small>}
      {value
        .map((p) => checkTerm(p.term) ?? (p.score === 0 || Math.abs(p.score) > 10000 ? `'${p.term}': use a score from -10000 to 10000, not 0.` : null))
        .filter((p): p is string => p !== null)
        .map((p) => (
          <small key={p} className={styles.termError}>
            {p}
          </small>
        ))}
    </div>
  )
}
