import { useQueryClient, type UseQueryResult } from '@tanstack/react-query'
import { useRef, useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { ApiError } from '../api/client'
import { usersApi, usersKeys, type CreatedInvite, type Invite, type InviteList, type InviteRole } from '../api/users'
import { useToasts } from '../activity/hooks'
import Icon from '../components/Icon'
import { Skeleton } from '../components/Loader'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import Toasts from '../indexerTools/Toast'
import { formatDate, formatDateTime, relativeTime } from '../lib/format'
import { ConfirmDialog } from './UserDialogs'
import { RoleChip } from './UsersTab'
import styles from './Users.module.css'

const stateLabel: Record<Invite['state'], { text: string; pill: string }> = {
  active: { text: 'Active', pill: ui.pillOk },
  expired: { text: 'Expired', pill: ui.pillWarn },
  usedUp: { text: 'Used', pill: '' },
  revoked: { text: 'Withdrawn', pill: ui.pillErr },
}

const expiryChoices = [
  { value: '1', label: '1 day' },
  { value: '7', label: '7 days' },
  { value: '30', label: '30 days' },
  { value: '', label: 'Never' },
]

/** Invite links: create (the link is shown once), list with uses and state, withdraw or remove. */
export default function InvitesTab({ query, adding, onAddingDone }: { query: UseQueryResult<InviteList>; adding: boolean; onAddingDone: () => void }) {
  const queryClient = useQueryClient()
  const [created, setCreated] = useState<CreatedInvite | null>(null)
  const [confirm, setConfirm] = useState<{ invite: Invite; remove: boolean } | null>(null)
  const { toasts, push, dismiss } = useToasts()
  const refresh = () => queryClient.invalidateQueries({ queryKey: usersKeys.invites })
  const records = query.data?.records ?? []

  return (
    <>
      {query.data && !query.data.formsSignIn && (
        <p className={ui.infoBox} style={{ marginBottom: 14 }}>
          <Icon name="info" size={16} />
          <span>
            Invite links need the sign-in page. Switch <Link to="/settings/general#security">Settings → General → Security</Link> to Forms, else the
            new accounts cannot sign in.
          </span>
        </p>
      )}
      {query.isError && <p className={ui.errBox}>{query.error.message}</p>}

      <div className={`${ui.panel} ${styles.panel}`}>
        {query.isPending ? (
          <div className={styles.empty}>
            <Skeleton rows={3} />
          </div>
        ) : records.length === 0 ? (
          <div className={`${ui.empty} ${styles.empty}`}>No invites yet. Create one and send the link to a friend.</div>
        ) : (
          <div className={styles.tableWrap}>
            <table className={`${ui.table} ${styles.table}`}>
              <thead>
                <tr>
                  <th>Invite</th>
                  <th className={styles.hideSm}>State</th>
                  <th className={styles.hideSm}>Uses</th>
                  <th className={styles.hideSm}>Expires</th>
                  <th className={styles.hideMd}>Created</th>
                  <th aria-label="Actions" />
                </tr>
              </thead>
              <tbody>
                {records.map((invite, i) => {
                  const state = stateLabel[invite.state]
                  return (
                    <tr key={invite.id} className={invite.state === 'active' ? '' : styles.dim} style={{ animationDelay: `${Math.min(i, 12) * 25}ms` }}>
                      <td>
                        <div className={styles.cellUser}>
                          <RoleChip role={invite.role} />
                          <span className={`${styles.note} ${invite.note ? '' : ui.muted}`} title={invite.note ?? undefined}>
                            {invite.note ?? 'No note'}
                            <small className={styles.showSm}>
                              {state.text} · {invite.uses} of {invite.maxUses}
                              {invite.expires ? ` · until ${formatDate(invite.expires)}` : ''}
                            </small>
                          </span>
                        </div>
                      </td>
                      <td className={`${styles.state} ${styles.hideSm}`}>
                        <span className={`${ui.pill} ${state.pill}`}>{state.text}</span>
                      </td>
                      <td className={`${styles.hideSm} ${ui.num}`}>
                        {invite.uses} of {invite.maxUses}
                      </td>
                      <td className={`${styles.hideSm} ${styles.when}`}>{invite.expires ? formatDate(invite.expires) : <span className={ui.muted}>Never</span>}</td>
                      <td className={`${styles.hideMd} ${styles.when}`} title={formatDateTime(invite.created)}>
                        {relativeTime(invite.created)}
                        {invite.createdByName && <span className={ui.muted}> · {invite.createdByName}</span>}
                      </td>
                      <td className={styles.actionsCell}>
                        {invite.state === 'active' ? (
                          <button type="button" className={`${ui.ghost} ${ui.sm}`} onClick={() => setConfirm({ invite, remove: false })} title="Withdraw" aria-label="Withdraw">
                            <Icon name="ban" size={15} />
                            <span className={styles.hideSm}>Withdraw</span>
                          </button>
                        ) : (
                          <button
                            type="button"
                            className={`${ui.ghost} ${ui.icon} ${ui.sm}`}
                            title="Remove from the list"
                            aria-label="Remove from the list"
                            onClick={() => setConfirm({ invite, remove: true })}
                          >
                            <Icon name="trash" size={15} />
                          </button>
                        )}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {adding && (
        <CreateInviteDialog
          onCreated={(invite) => {
            setCreated(invite)
            onAddingDone()
            void refresh()
          }}
          onClose={onAddingDone}
        />
      )}
      {created && <InviteLinkDialog invite={created} onClose={() => setCreated(null)} />}
      {confirm && (
        <ConfirmDialog
          title={confirm.remove ? 'Remove invite' : 'Withdraw invite'}
          confirm={confirm.remove ? 'Remove' : 'Withdraw'}
          danger={!confirm.remove}
          onConfirm={async () => {
            await (confirm.remove ? usersApi.deleteInvite(confirm.invite.id) : usersApi.revokeInvite(confirm.invite.id))
            if (!confirm.remove) push('ok', 'Invite withdrawn', 'The link no longer works.')
            await refresh()
          }}
          onClose={() => setConfirm(null)}
        >
          {confirm.remove
            ? 'Removes the invite from the list. Accounts created with it stay.'
            : 'The link stops working at once. Accounts already created with it stay.'}
        </ConfirmDialog>
      )}
      <Toasts toasts={toasts} onDismiss={dismiss} />
    </>
  )
}

function CreateInviteDialog({ onCreated, onClose }: { onCreated: (invite: CreatedInvite) => void; onClose: () => void }) {
  const [role, setRole] = useState<InviteRole>('requester')
  const [note, setNote] = useState('')
  const [expires, setExpires] = useState('7')
  const [maxUses, setMaxUses] = useState('1')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<ApiError | Error | null>(null)
  const fields = error instanceof ApiError ? error.fieldErrors : {}

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      onCreated(
        await usersApi.createInvite({
          role,
          note: note.trim() || null,
          expiresInDays: expires ? Number(expires) : null,
          maxUses: maxUses.trim() ? Number(maxUses) : null,
        }),
      )
    } catch (err) {
      setError(err instanceof Error ? err : new Error(String(err)))
      setBusy(false)
    }
  }

  return (
    <Modal title="Create invite" onClose={onClose}>
      <form className={styles.form} onSubmit={(e) => void submit(e)} noValidate>
        <div className={styles.group}>
          <div className={styles.groupHead}>
            <span>
              <b>Role</b>
              <small className={ui.muted}>
                {role === 'requester' ? 'Discover and requests only.' : 'Library, downloads and requests; no settings.'}
              </small>
            </span>
            <div className={ui.seg} role="group" aria-label="Role">
              <button type="button" aria-pressed={role === 'requester'} onClick={() => setRole('requester')}>
                Requester
              </button>
              <button type="button" aria-pressed={role === 'friend'} onClick={() => setRole('friend')}>
                Friend
              </button>
            </div>
          </div>
        </div>
        <label className={ui.field}>
          Note
          <input className={ui.input} value={note} onChange={(e) => setNote(e.target.value)} placeholder="For whom, e.g. “Jane from work”" maxLength={200} autoFocus />
          {fields.note ? <small className={styles.fieldError}>{fields.note[0]}</small> : <small>Only admins see it, and the invite page.</small>}
        </label>
        <div className={styles.grid2}>
          <label className={ui.field}>
            Expires after
            <select className={ui.select} value={expires} onChange={(e) => setExpires(e.target.value)}>
              {expiryChoices.map((c) => (
                <option key={c.value} value={c.value}>
                  {c.label}
                </option>
              ))}
            </select>
            {fields.expiresInDays && <small className={styles.fieldError}>{fields.expiresInDays[0]}</small>}
          </label>
          <label className={ui.field}>
            Accounts
            <input className={`${ui.input} ${ui.num}`} type="number" min={1} max={100} value={maxUses} onChange={(e) => setMaxUses(e.target.value)} />
            {fields.maxUses ? <small className={styles.fieldError}>{fields.maxUses[0]}</small> : <small>How many people may use the link.</small>}
          </label>
        </div>
        {error && Object.keys(fields).length === 0 && (
          <p className={ui.errBox} role="alert">
            <Icon name="alert" size={16} />
            <span>{error.message}</span>
          </p>
        )}
        <div className={styles.dialogActions}>
          <button type="button" className={ui.button} onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className={ui.primary} disabled={busy}>
            <Icon name="send" size={15} />
            {busy ? 'Creating…' : 'Create link'}
          </button>
        </div>
      </form>
    </Modal>
  )
}

/** The new link, with copy; StreamZH only keeps a hash, so it cannot be shown again. */
function InviteLinkDialog({ invite, onClose }: { invite: CreatedInvite; onClose: () => void }) {
  const input = useRef<HTMLInputElement>(null)
  const [copied, setCopied] = useState(false)
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(invite.url)
      setCopied(true)
    } catch {
      // No clipboard (plain http): select the text so Ctrl+C works.
      input.current?.select()
    }
  }

  return (
    <Modal title="Invite link" onClose={onClose}>
      <div className={styles.form}>
        <p className={styles.dialogText}>
          Send this link to the person you invite. They choose a username and password and are signed in as{' '}
          <b>{invite.invite.role === 'friend' ? 'friend' : 'requester'}</b>
          {invite.invite.expires ? `, until ${formatDate(invite.invite.expires)}` : ''}.
        </p>
        <div className={styles.linkBox}>
          <input ref={input} className={ui.input} value={invite.url} readOnly onFocus={(e) => e.target.select()} aria-label="Invite link" />
          <button type="button" className={copied ? ui.button : ui.primary} onClick={() => void copy()}>
            <Icon name={copied ? 'check' : 'copy'} size={15} />
            {copied ? 'Copied' : 'Copy'}
          </button>
        </div>
        <p className={styles.warnLine}>
          <Icon name="alert" size={15} />
          The link is shown only now. Anyone with it can create an account.
        </p>
        <div className={styles.dialogActions}>
          <button type="button" className={ui.button} onClick={onClose}>
            Done
          </button>
        </div>
      </div>
    </Modal>
  )
}
