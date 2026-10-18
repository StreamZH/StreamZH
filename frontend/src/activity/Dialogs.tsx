import { useQuery } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { downloadSettingsApi, downloadSettingsKeys } from '../api/downloadSettings'
import { downloadsApi, type DownloadItem } from '../api/downloads'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import { ConfirmRemove } from './DownloadCard'
import styles from './Activity.module.css'

/** "Remove download" with the choice to delete the files too. */
export function RemoveDialog({
  item,
  busy,
  onConfirm,
  onClose,
}: {
  item: DownloadItem
  busy: boolean
  onConfirm: (deleteFiles: boolean) => void
  onClose: () => void
}) {
  const [deleteFiles, setDeleteFiles] = useState(item.status !== 'completed')
  return (
    <Modal title="Remove download" onClose={onClose}>
      <ConfirmRemove item={item} deleteFiles={deleteFiles} onDeleteFiles={setDeleteFiles} />
      <div className={styles.dialogActions}>
        <button type="button" className={ui.button} onClick={onClose}>
          Cancel
        </button>
        <button type="button" className={ui.dangerSolid} onClick={() => onConfirm(deleteFiles)} disabled={busy}>
          {busy ? 'Removing…' : 'Remove'}
        </button>
      </div>
    </Modal>
  )
}

/** Manual NZB upload (POST /download, multipart). */
export function UploadDialog({ onClose, onUploaded }: { onClose: () => void; onUploaded: (title: string) => void }) {
  const [file, setFile] = useState<File | null>(null)
  const [title, setTitle] = useState('')
  const [category, setCategory] = useState('other')
  const categories = useQuery({ queryKey: downloadSettingsKeys.choices, queryFn: downloadSettingsApi.choices })
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    if (!file) {
      setError('Choose an .nzb file.')
      return
    }
    setBusy(true)
    setError(null)
    try {
      await downloadsApi.upload(file, title.trim() || undefined, category)
      onUploaded(title.trim() || file.name.replace(/\.nzb$/i, ''))
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
      setBusy(false)
    }
  }

  return (
    <Modal title="Add NZB" onClose={onClose}>
      <form className={styles.uploadForm} onSubmit={(e) => void submit(e)}>
        <p className={styles.dialogText}>StreamZH downloads it right away with your Usenet servers. It is not linked to a movie or series.</p>
        <label className={ui.field}>
          NZB file
          <input
            className={`${ui.input} ${styles.fileInput}`}
            type="file"
            accept=".nzb,application/x-nzb"
            onChange={(e) => {
              setFile(e.target.files?.[0] ?? null)
              setError(null)
            }}
          />
        </label>
        <label className={ui.field}>
          Name <small>Optional; the file name is used otherwise.</small>
          <input className={ui.input} value={title} onChange={(e) => setTitle(e.target.value)} placeholder={file?.name.replace(/\.nzb$/i, '') ?? ''} />
        </label>
        <label className={ui.field}>
          Category
          <select className={ui.select} value={category} onChange={(e) => setCategory(e.target.value)}>
            {(categories.data ?? [{ name: 'movies' }, { name: 'series' }, { name: 'other' }]).map((c) => (
              <option key={c.name} value={c.name}>
                {c.name}
              </option>
            ))}
          </select>
        </label>
        {error && (
          <p className={ui.error} role="alert">
            {error}
          </p>
        )}
        <div className={styles.dialogActions}>
          <button type="button" className={ui.button} onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className={ui.primary} disabled={busy}>
            {busy ? 'Uploading…' : 'Add to queue'}
          </button>
        </div>
      </form>
    </Modal>
  )
}
