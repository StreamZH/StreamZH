import { useState } from 'react'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import styles from './Detail.module.css'

/** CloudZH .pathrow: a small label over a path in mono, with a copy button. */
export default function PathRow({ label, path, empty }: { label: string; path: string | null; empty?: string }) {
  const [copied, setCopied] = useState(false)
  const copy = () => {
    if (!path || !navigator.clipboard) return
    void navigator.clipboard.writeText(path).then(() => {
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    })
  }

  return (
    <div className={styles.pathrow}>
      <div>
        <small>{label}</small>
        {path ? <code>{path}</code> : <span className={styles.none}>{empty ?? '–'}</span>}
      </div>
      {path && (
        <button
          type="button"
          className={`${ui.ghost} ${ui.sm} ${ui.icon}`}
          onClick={copy}
          aria-label={copied ? 'Copied' : `Copy ${label.toLowerCase()}`}
          title={copied ? 'Copied' : 'Copy'}
        >
          <Icon name={copied ? 'check' : 'copy'} size={15} />
        </button>
      )}
    </div>
  )
}
