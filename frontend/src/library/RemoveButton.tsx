import { useState } from 'react'
import ui from '../components/ui.module.css'
import { useExclusionOnRemove, type ListItemRef } from '../importLists/useExclusionOnRemove'

/** `item`: when an import list added it, the confirmation offers to add it to the list exclusions. */
export default function RemoveButton({ onConfirm, pending, item }: { onConfirm: () => void; pending: boolean; item?: ListItemRef }) {
  const [confirming, setConfirming] = useState(false)
  const exclusion = useExclusionOnRemove(confirming ? item : undefined)

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
      {exclusion.prompt}
      <button className={ui.danger} type="button" onClick={() => void exclusion.apply().finally(onConfirm)} disabled={pending}>
        Yes, remove
      </button>
      <button className={ui.button} type="button" onClick={() => setConfirming(false)}>
        Cancel
      </button>
    </span>
  )
}
