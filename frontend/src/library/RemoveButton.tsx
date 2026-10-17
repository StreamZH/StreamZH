import { useState } from 'react'
import ui from '../components/ui.module.css'

export default function RemoveButton({ onConfirm, pending }: { onConfirm: () => void; pending: boolean }) {
  const [confirming, setConfirming] = useState(false)

  if (!confirming) {
    return (
      <button className={ui.button} type="button" onClick={() => setConfirming(true)}>
        Remove
      </button>
    )
  }

  return (
    <span style={{ display: 'inline-flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
      <span className={ui.muted} style={{ fontSize: 13 }}>
        Removes it from StreamZH. Files on disk stay.
      </span>
      <button className={ui.danger} type="button" onClick={onConfirm} disabled={pending}>
        Yes, remove
      </button>
      <button className={ui.button} type="button" onClick={() => setConfirming(false)}>
        Cancel
      </button>
    </span>
  )
}
