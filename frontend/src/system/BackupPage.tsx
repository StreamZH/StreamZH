import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useRef, useState, type ChangeEvent } from 'react'
import { Link } from 'react-router'
import { generalApi, generalKeys, type BackupInfo } from '../api/general'
import { statusKeys } from '../api/status'
import Icon from '../components/Icon'
import { Skeleton } from '../components/Loader'
import ui from '../components/ui.module.css'
import { formatBytes, formatDateTime, relativeTime } from '../lib/format'
import { Section } from '../settings/SettingsKit'
import { RestartControls } from './Restart'
import styles from './System.module.css'
import { SystemPage } from './SystemPage'

/** System → Backup (like Radarr): backups of the database and settings; download, delete, restore. */
export default function BackupPage() {
  const queryClient = useQueryClient()
  const list = useQuery({ queryKey: generalKeys.backups, queryFn: generalApi.backups })
  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: generalKeys.backups })
    void queryClient.invalidateQueries({ queryKey: statusKeys.ongoing })
  }
  const backupNow = useMutation({ mutationFn: generalApi.backupNow, onSuccess: refresh })
  const cancel = useMutation({ mutationFn: generalApi.cancelRestore, onSuccess: refresh })
  const upload = useMutation({ mutationFn: generalApi.uploadRestore, onSuccess: refresh })
  const fileInput = useRef<HTMLInputElement>(null)
  const data = list.data

  const onFile = (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0]
    event.target.value = ''
    if (file) upload.mutate(file)
  }

  return (
    <SystemPage
      title="Backup"
      lead="The database with all settings, library and history, as a zip file. Your media files are not part of it."
      actions={
        <button className={ui.primary} type="button" onClick={() => backupNow.mutate()} disabled={backupNow.isPending}>
          {backupNow.isPending ? <span className={styles.spinner} /> : <Icon name="archive" size={15} />}
          {backupNow.isPending ? 'Backing up…' : 'Backup now'}
        </button>
      }
    >
      {backupNow.error && <p className={ui.error}>{backupNow.error.message}</p>}
      {list.isError && <p className={ui.error}>{list.error.message}</p>}

      {data?.pendingRestore && (
        <div className={styles.banner} role="status">
          <div className={styles.bannerText}>
            <strong>Restore waiting for a restart</strong>
            <small>
              {data.pendingRestore.source}
              {data.pendingRestore.version && ` (StreamZH ${data.pendingRestore.version})`} replaces the current database when StreamZH starts again. The current
              one is kept in the data folder under restore.
            </small>
          </div>
          <RestartControls compact />
          <button className={ui.button} type="button" onClick={() => cancel.mutate()} disabled={cancel.isPending}>
            Cancel restore
          </button>
        </div>
      )}

      <Section
        id="backups"
        title="Backups"
        description={
          data && (
            <>
              {data.intervalDays > 0 ? `Every ${data.intervalDays === 1 ? 'day' : `${data.intervalDays} days`}` : 'No scheduled backups'}, kept {data.retentionDays} days, in{' '}
              <span className={styles.mono}>{data.folder}</span>. Change it in <Link to="/settings/general#backups">Settings → General</Link>.
            </>
          )
        }
      >
        {list.isPending && <Skeleton rows={4} />}
        {data && data.backups.length === 0 && (
          <div className={ui.empty}>
            <Icon name="archive" size={22} />
            <span>No backups yet.</span>
          </div>
        )}
        {data && data.backups.length > 0 && (
          <div className={`${styles.table} ${styles.backups}`} role="table" aria-label="Backups">
            <div className={styles.head} role="row">
              <span role="columnheader">Name</span>
              <span role="columnheader">Type</span>
              <span role="columnheader" className={styles.right}>
                Size
              </span>
              <span role="columnheader">Time</span>
              <span role="columnheader" />
            </div>
            {data.backups.map((backup) => (
              <BackupRow key={`${backup.type}/${backup.name}`} backup={backup} onChange={refresh} />
            ))}
          </div>
        )}
      </Section>

      <Section
        id="restore"
        title="Restore from a file"
        description="A backup zip from this or another StreamZH. It is checked first and restored at the next start; backups from a newer StreamZH are refused."
      >
        <div className={styles.actions}>
          <input ref={fileInput} className={styles.hiddenInput} type="file" accept=".zip,application/zip" onChange={onFile} />
          <button className={ui.button} type="button" onClick={() => fileInput.current?.click()} disabled={upload.isPending}>
            {upload.isPending ? <span className={styles.spinner} /> : <Icon name="upload" size={15} />}
            {upload.isPending ? 'Checking…' : 'Choose backup file…'}
          </button>
          {upload.error && <span className={ui.error}>{upload.error.message}</span>}
          {upload.isSuccess && <span className={ui.ok}>Checked and staged. Restart to restore.</span>}
        </div>
      </Section>
    </SystemPage>
  )
}

function BackupRow({ backup, onChange }: { backup: BackupInfo; onChange: () => void }) {
  const [confirm, setConfirm] = useState<'restore' | 'delete' | null>(null)
  const restore = useMutation({
    mutationFn: () => generalApi.restoreBackup(backup),
    onSuccess: () => {
      setConfirm(null)
      onChange()
    },
  })
  const remove = useMutation({ mutationFn: () => generalApi.deleteBackup(backup), onSuccess: onChange })
  const error = restore.error ?? remove.error

  return (
    <div className={styles.row} role="row">
      <span role="cell" className={`${styles.cell} ${styles.fileCell}`}>
        <span className={styles.fileIcon} aria-hidden="true">
          <Icon name="filezip" size={15} />
        </span>
        <span className={styles.mono}>{backup.name}</span>
        {error && <div className={`${styles.errorText} ${styles.small}`}>{error.message}</div>}
      </span>
      <span role="cell" className={styles.cell}>
        <span className={styles.cellLabel}>Type</span>
        <span className={ui.tag}>{backup.type === 'scheduled' ? 'Scheduled' : 'Manual'}</span>
      </span>
      <span role="cell" className={`${styles.cell} ${styles.right}`}>
        <span className={styles.cellLabel}>Size</span>
        {formatBytes(backup.size)}
      </span>
      <span role="cell" className={styles.cell} title={formatDateTime(backup.time)}>
        <span className={styles.cellLabel}>Time</span>
        {relativeTime(backup.time)}
      </span>
      <span role="cell" className={`${styles.cell} ${styles.actions}`}>
        {confirm === 'restore' ? (
          <>
            <button className={`${ui.primary} ${ui.sm}`} type="button" onClick={() => restore.mutate()} disabled={restore.isPending}>
              Restore at restart
            </button>
            <button className={`${ui.ghost} ${ui.sm}`} type="button" onClick={() => setConfirm(null)}>
              Cancel
            </button>
          </>
        ) : confirm === 'delete' ? (
          <>
            <button className={`${ui.danger} ${ui.sm}`} type="button" onClick={() => remove.mutate()} disabled={remove.isPending}>
              Delete
            </button>
            <button className={`${ui.ghost} ${ui.sm}`} type="button" onClick={() => setConfirm(null)}>
              Cancel
            </button>
          </>
        ) : (
          <>
            <a className={`${ui.button} ${ui.sm}`} href={generalApi.backupUrl(backup)} download={backup.name}>
              <Icon name="download" size={14} />
              Download
            </a>
            <button className={`${ui.button} ${ui.sm}`} type="button" onClick={() => setConfirm('restore')}>
              <Icon name="rollback" size={14} />
              Restore
            </button>
            <button
              className={`${ui.ghost} ${ui.sm} ${ui.icon}`}
              type="button"
              onClick={() => setConfirm('delete')}
              aria-label={`Delete ${backup.name}`}
              title="Delete"
            >
              <Icon name="trash" size={14} />
            </button>
          </>
        )}
      </span>
    </div>
  )
}
