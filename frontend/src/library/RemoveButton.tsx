import { useState } from 'react'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import { useExclusionOnRemove, type ListItemRef } from '../importLists/useExclusionOnRemove'
import styles from './ReleaseSearch.module.css'

/** `item`: when an import list added it, the confirmation offers to add it to the list exclusions. */
export default function RemoveButton({ onConfirm, pending, item }: { onConfirm: () => void; pending: boolean; item?: ListItemRef }) {
  const [confirming, setConfirming] = useState(false)
  const exclusion = useExclusionOnRemove(confirming ? item : undefined)

  if (!confirming) {
    return (
      <button className={ui.button} type="button" onClick={() => setConfirming(true)}>
        <Icon name="trash" size={15} />
        Remove
      </button>
    )
  }

  return (
    <span className={styles.confirmRemove}>
      <span className={ui.muted}>Removes it from StreamZH. Files on disk stay.</span>
      {exclusion.prompt}
      <button className={`${ui.danger} ${ui.sm}`} type="button" onClick={() => void exclusion.apply().finally(onConfirm)} disabled={pending}>
        Yes, remove
      </button>
      <button className={`${ui.ghost} ${ui.sm}`} type="button" onClick={() => setConfirming(false)}>
        Cancel
      </button>
    </span>
  )
}
