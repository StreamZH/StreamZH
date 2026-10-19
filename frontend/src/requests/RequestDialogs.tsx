import { useQuery } from '@tanstack/react-query'
import { useId, useState } from 'react'
import { libraryApi, queryKeys } from '../api/library'
import { seasonsText, type ApproveOptions, type MediaRequest } from '../api/requests'
import Icon from '../components/Icon'
import Modal from '../components/Modal'
import ProfileSelect from '../components/ProfileSelect'
import ui from '../components/ui.module.css'
import styles from './Requests.module.css'

const label = (r: MediaRequest) => `${r.title}${r.year ? ` (${r.year})` : ''}`

/** Approve with another quality profile or root folder than the defaults (Advanced). */
export function ApproveDialog({ request: r, pending, error, onApprove, onClose }: {
  request: MediaRequest
  pending: boolean
  error: string | null
  onApprove: (options: ApproveOptions) => void
  onClose: () => void
}) {
  const folders = useQuery({ queryKey: queryKeys.rootFolders, queryFn: libraryApi.rootFolders })
  const type = r.mediaType === 'movie' ? 'Movie' : 'Series'
  const choices = (folders.data ?? []).filter((f) => f.mediaType === type)
  const [profileId, setProfileId] = useState<number | null>(r.qualityProfileId)
  const [folderId, setFolderId] = useState<number | null>(r.rootFolderId)
  const folderSelect = useId()
  const seasons = seasonsText(r.mediaType, r.seasons)

  return (
    <Modal title="Approve request" onClose={onClose}>
      <div className={styles.dialog}>
        <p className={styles.dialogLead}>
          <b>{label(r)}</b>
          {seasons && ` · ${seasons}`}
        </p>
        <div className={styles.dialogFields}>
          <label className={ui.field}>
            <span>Quality profile</span>
            <ProfileSelect value={profileId} onChange={setProfileId} />
          </label>
          <label className={ui.field} htmlFor={folderSelect}>
            <span>Root folder</span>
            <select
              id={folderSelect}
              className={ui.select}
              value={folderId ?? ''}
              onChange={(e) => setFolderId(e.target.value === '' ? null : Number(e.target.value))}
            >
              <option value="">Default from Settings → Requests</option>
              {choices.map((f) => (
                <option key={f.id} value={f.id}>
                  {f.path}
                </option>
              ))}
            </select>
          </label>
        </div>
        <p className={styles.dialogHelp}>Already in the library? Then StreamZH keeps its profile and folder and only monitors what was requested.</p>
        {error && (
          <div className={ui.errBox} role="alert">
            <Icon name="alert" size={15} />
            {error}
          </div>
        )}
        <div className={styles.dialogActions}>
          <button type="button" className={ui.ghost} onClick={onClose}>
            Cancel
          </button>
          <button type="button" className={ui.primary} disabled={pending} onClick={() => onApprove({ qualityProfileId: profileId, rootFolderId: folderId })}>
            <Icon name="check" size={15} />
            {pending ? 'Approving…' : 'Approve'}
          </button>
        </div>
      </div>
    </Modal>
  )
}

/** Decline with an optional reason the requester sees. */
export function DeclineDialog({ request: r, pending, error, onDecline, onClose }: {
  request: MediaRequest
  pending: boolean
  error: string | null
  onDecline: (reason: string | null) => void
  onClose: () => void
}) {
  const [reason, setReason] = useState('')
  const id = useId()
  return (
    <Modal title="Decline request" onClose={onClose}>
      <div className={styles.dialog}>
        <p className={styles.dialogLead}>
          <b>{label(r)}</b> · requested by {r.requestedByName || r.discordName || 'a Discord guest'}
        </p>
        <label className={ui.field} htmlFor={id}>
          <span>
            Reason <span className={ui.muted}>(optional, the requester sees it)</span>
          </span>
          <textarea
            id={id}
            className={`${ui.input} ${styles.textarea}`}
            rows={3}
            maxLength={500}
            placeholder="Not available in a good quality yet"
            value={reason}
            onChange={(e) => setReason(e.target.value)}
          />
        </label>
        {error && (
          <div className={ui.errBox} role="alert">
            <Icon name="alert" size={15} />
            {error}
          </div>
        )}
        <div className={styles.dialogActions}>
          <button type="button" className={ui.ghost} onClick={onClose}>
            Cancel
          </button>
          <button type="button" className={ui.dangerSolid} disabled={pending} onClick={() => onDecline(reason.trim() || null)}>
            <Icon name="ban" size={15} />
            {pending ? 'Declining…' : 'Decline'}
          </button>
        </div>
      </div>
    </Modal>
  )
}

/** Confirms cancelling (own pending request) or removing (admin) a request. */
export function RemoveRequestDialog({ request: r, own, pending, error, onConfirm, onClose }: {
  request: MediaRequest
  own: boolean
  pending: boolean
  error: string | null
  onConfirm: () => void
  onClose: () => void
}) {
  return (
    <Modal title={own && r.status === 'pending' ? 'Cancel request?' : 'Remove request?'} onClose={onClose}>
      <div className={styles.dialog}>
        <p className={styles.dialogLead}>
          {own && r.status === 'pending' ? (
            <>
              <b>{label(r)}</b> will not be added. You can request it again later.
            </>
          ) : (
            <>
              The request for <b>{label(r)}</b> disappears from the list. Nothing in the library changes.
            </>
          )}
        </p>
        {error && (
          <div className={ui.errBox} role="alert">
            <Icon name="alert" size={15} />
            {error}
          </div>
        )}
        <div className={styles.dialogActions}>
          <button type="button" className={ui.ghost} onClick={onClose}>
            Keep it
          </button>
          <button type="button" className={ui.dangerSolid} disabled={pending} onClick={onConfirm}>
            <Icon name={own && r.status === 'pending' ? 'x' : 'trash'} size={15} />
            {pending ? 'Working…' : own && r.status === 'pending' ? 'Cancel request' : 'Remove'}
          </button>
        </div>
      </div>
    </Modal>
  )
}
