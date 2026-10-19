import { useState } from 'react'
import type { DownloadItem } from '../api/downloads'
import Icon from '../components/Icon'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import { formatBytes } from '../lib/format'
import { formatSpeed } from '../status/format'
import { ProtocolIcon, Ratio } from './torrent'
import { clientNameOf } from './torrentFormat'
import styles from './TorrentActivity.module.css'

// Torrent parts of Activity: which client has the download, the seeding badge, ratio and upload, and "Remove from
// client" with the choice to keep or delete the data.

/** Magnet icon and the client's name ("qBittorrent", "StreamZH"). */
export function ClientChip({ item }: { item: DownloadItem }) {
  return (
    <span className={styles.client} title={`Torrent in ${clientNameOf(item) ?? 'a torrent client'}`}>
      <ProtocolIcon protocol="torrent" size={12} label={false} />
      {clientNameOf(item) ?? 'Torrent'}
    </span>
  )
}

/** "Seeding" pill with an upload arrow (CloudZH .pill in the info tone). `className`/`textClass`: the list's own pill styles. */
export function SeedingPill({ className = '', textClass }: { className?: string; textClass?: string }) {
  return (
    <span className={`${ui.pill} ${styles.seeding} ${className}`} title="Done and still uploading to other peers">
      <Icon name="upload" size={13} />
      <span className={textClass}>Seeding</span>
    </span>
  )
}

/** "Ratio 1.24 · 6.1 GB up · 1.2 MB/s up". */
export function SeedStats({ item }: { item: DownloadItem }) {
  return (
    <span className={styles.stats}>
      <span>
        <span className={styles.label}>Ratio</span> <Ratio ratio={item.ratio} />
      </span>
      {item.uploadedBytes != null && (
        <span>
          <Icon name="upload" size={13} />
          {formatBytes(item.uploadedBytes)} up
        </span>
      )}
      {item.seeding && item.uploadSpeedBps ? <span>{formatSpeed(item.uploadSpeedBps)}</span> : null}
    </span>
  )
}

/**
 * "Remove from client": removes the torrent from its client and the download from StreamZH. Keep the data (default
 * once complete: the client may still seed it elsewhere, imported files are untouched) or delete it too.
 */
export function RemoveFromClientDialog({
  item,
  busy,
  onConfirm,
  onClose,
}: {
  item: DownloadItem
  busy: boolean
  onConfirm: (deleteData: boolean) => void
  onClose: () => void
}) {
  const done = item.status === 'completed'
  const [deleteData, setDeleteData] = useState(!done)
  const client = clientNameOf(item) ?? 'the torrent client'
  return (
    <Modal title="Remove from client" onClose={onClose}>
      <div className={styles.dialog}>
        <p className={styles.text}>
          Removes <span className={ui.mono}>{item.title}</span> from {client} and from StreamZH&apos;s {done || item.status === 'failed' ? 'history' : 'queue'}.
          {item.seeding && ' Seeding stops; private trackers may count that against your account if the seed time is not reached.'}
        </p>
        <div className={styles.choices} role="radiogroup" aria-label="Downloaded data">
          <label className={`${styles.choice} ${!deleteData ? styles.choiceOn : ''}`}>
            <input type="radio" name="torrent-data" checked={!deleteData} onChange={() => setDeleteData(false)} />
            <span>
              <b>Keep the data</b>
              <small>The files stay in the client&apos;s download folder.</small>
            </span>
          </label>
          <label className={`${styles.choice} ${deleteData ? styles.choiceOn : ''}`}>
            <input type="radio" name="torrent-data" checked={deleteData} onChange={() => setDeleteData(true)} />
            <span>
              <b>Delete the data too</b>
              <small>{done ? 'Imported files in your library are not touched: they are hardlinks or copies.' : 'The partly downloaded files are deleted.'}</small>
            </span>
          </label>
        </div>
        <div className={styles.actions}>
          <button type="button" className={ui.button} onClick={onClose}>
            Cancel
          </button>
          <button type="button" className={ui.dangerSolid} onClick={() => onConfirm(deleteData)} disabled={busy}>
            {busy ? 'Removing…' : deleteData ? 'Remove and delete data' : 'Remove from client'}
          </button>
        </div>
      </div>
    </Modal>
  )
}
