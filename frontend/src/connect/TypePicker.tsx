import type { ConnectKind, ConnectType } from '../api/connect'
import ui from '../components/ui.module.css'
import Dialog from '../indexers/Dialog'
import { kindLabels } from './labels'
import styles from './Connect.module.css'

/** "Add connection": the catalog of one kind (media servers or notifications). */
export default function TypePicker({
  types,
  kind,
  onSelect,
  onClose,
}: {
  types: ConnectType[]
  kind: ConnectKind
  onSelect: (type: ConnectType) => void
  onClose: () => void
}) {
  return (
    <Dialog
      title={kind === 'mediaServer' ? 'Add media server' : 'Add notification'}
      subtitle={`${kindLabels[kind]} StreamZH can talk to`}
      size="wide"
      onClose={onClose}
      footer={
        <>
          <span className={styles.spacer} />
          <button className={ui.button} type="button" onClick={onClose}>
            Close
          </button>
        </>
      }
    >
      <ul className={styles.typeGrid}>
        {types
          .filter((t) => t.kind === kind)
          .map((t) => (
            <li key={t.key}>
              <button type="button" className={styles.typeCard} onClick={() => onSelect(t)}>
                <span className={styles.typeName}>{t.name}</span>
                <span className={styles.typeText}>{t.description}</span>
              </button>
            </li>
          ))}
      </ul>
    </Dialog>
  )
}
