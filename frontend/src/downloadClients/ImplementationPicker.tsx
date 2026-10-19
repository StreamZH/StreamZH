import { builtInImplementation, knownImplementations, type ClientImplementation, type DownloadClient } from '../api/downloadClients'
import ui from '../components/ui.module.css'
import Dialog from '../indexers/Dialog'
import { ClientMark } from './ClientMark'
import styles from './DownloadClients.module.css'

/** "Add torrent client": the implementations the server offers (built-in engine first), as cards. */
export default function ImplementationPicker({
  types,
  existing,
  onSelect,
  onClose,
}: {
  types: ClientImplementation[]
  existing: DownloadClient[]
  onSelect: (type: ClientImplementation) => void
  onClose: () => void
}) {
  const order = (t: ClientImplementation) => {
    const index = knownImplementations.findIndex((k) => k.implementation === t.implementation)
    return index < 0 ? knownImplementations.length : index
  }
  const torrent = types.filter((t) => t.protocol === 'torrent').sort((a, b) => order(a) - order(b) || a.name.localeCompare(b.name))
  const hasBuiltIn = existing.some((c) => c.implementation === builtInImplementation)

  return (
    <Dialog
      title="Add torrent client"
      subtitle="Where StreamZH sends torrents from your Torznab indexers"
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
      {torrent.length === 0 ? (
        <p className={ui.muted}>This server offers no torrent clients.</p>
      ) : (
        <ul className={styles.typeGrid}>
          {torrent.map((t) => {
            const builtIn = t.implementation === builtInImplementation
            const taken = builtIn && hasBuiltIn
            return (
              <li key={t.implementation}>
                <button type="button" className={`${styles.typeCard} ${builtIn ? styles.typeBuiltIn : ''}`} onClick={() => onSelect(t)} disabled={taken}>
                  <span className={styles.typeHead}>
                    <ClientMark implementation={t.implementation} />
                    <span className={styles.typeName}>{t.name}</span>
                    {builtIn && <span className={styles.typeBadge}>{taken ? 'Added' : 'No setup'}</span>}
                  </span>
                  <span className={styles.typeText}>{t.description}</span>
                </button>
              </li>
            )
          })}
        </ul>
      )}
      <p className={styles.pickerNote}>
        Usenet downloads keep running in StreamZH&apos;s own engine. External clients need their Web UI or RPC switched on, reachable from this
        server.
      </p>
    </Dialog>
  )
}
