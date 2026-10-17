import { useRef, useState } from 'react'
import ui from '../components/ui.module.css'
import styles from './MediaManagement.module.css'
import { tokenGroups, type TokenKind } from './tokens'

/**
 * A naming format: monospace input, a token list that inserts at the cursor (like Radarr's token picker), and the
 * example name the server builds from it.
 */
export default function FormatField({
  id,
  value,
  onChange,
  kind,
  example,
  exampleLabel = 'Example',
}: {
  id: string
  value: string
  onChange: (value: string) => void
  kind: TokenKind
  example?: string | null
  exampleLabel?: string
}) {
  const [open, setOpen] = useState(false)
  const input = useRef<HTMLInputElement>(null)

  const insert = (token: string) => {
    const el = input.current
    const start = el?.selectionStart ?? value.length
    const end = el?.selectionEnd ?? value.length
    const next = value.slice(0, start) + token + value.slice(end)
    onChange(next)
    requestAnimationFrame(() => {
      el?.focus()
      el?.setSelectionRange(start + token.length, start + token.length)
    })
  }

  return (
    <div className={styles.format}>
      <div className={styles.formatInput}>
        <input
          id={id}
          ref={input}
          className={`${ui.input} ${ui.mono}`}
          value={value}
          spellCheck={false}
          autoComplete="off"
          onChange={(e) => onChange(e.target.value)}
        />
        <button
          type="button"
          className={`${ui.button} ${open ? styles.tokensOpen : ''}`}
          aria-expanded={open}
          aria-controls={`${id}-tokens`}
          onClick={() => setOpen((o) => !o)}
        >
          {'{ }'} Tokens
        </button>
      </div>
      {example != null && example !== '' && (
        <p className={styles.example}>
          <span>{exampleLabel}</span>
          <code>{example}</code>
        </p>
      )}
      {open && (
        <div id={`${id}-tokens`} className={styles.tokens}>
          {tokenGroups[kind].map((group) => (
            <div key={group.name} className={styles.tokenGroup}>
              <span className={styles.tokenGroupName}>{group.name}</span>
              <div className={styles.tokenList}>
                {group.tokens.map((t) => (
                  <button key={t.token} type="button" className={styles.token} onClick={() => insert(t.token)} title={`Insert ${t.token}`}>
                    <code>{t.token}</code>
                    <small>{t.example}</small>
                  </button>
                ))}
              </div>
            </div>
          ))}
          <p className={styles.tokenHelp}>
            <code>{'{Movie.Title}'}</code> uses dots instead of spaces, <code>{'{MOVIE TITLE}'}</code> upper case. Text inside the braces, as in{' '}
            <code>{'{[Quality Full]}'}</code> or <code>{'{-Release Group}'}</code>, only shows when there is a value. Use <code>{'{{'}</code> and{' '}
            <code>{'}}'}</code> for literal braces.
          </p>
        </div>
      )}
    </div>
  )
}
