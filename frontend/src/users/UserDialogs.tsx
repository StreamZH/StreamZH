import { useState, type FormEvent, type ReactNode } from 'react'
import { ApiError } from '../api/client'
import { nameOf, roleHelp, roleNames, type Role, type UserAccount, type UserInput } from '../api/users'
import PasswordInput from '../auth/PasswordInput'
import Icon from '../components/Icon'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import styles from './Users.module.css'

type Approve = 'default' | 'on' | 'off'

/** Add or edit an account: role, profile, state, auto-approve and request limits. */
export function UserDialog({
  user,
  isOwner,
  self,
  actions,
  onSave,
  onClose,
}: {
  /** More actions for an existing account (reset password, sign out, delete), left of Cancel and Save. */
  actions?: ReactNode
  /** null: a new account. */
  user: UserAccount | null
  /** The signed-in user is an owner (only owners create or change owners). */
  isOwner: boolean
  /** The account is the signed-in user's own (no role change, no disabling). */
  self: boolean
  onSave: (input: UserInput) => Promise<unknown>
  onClose: () => void
}) {
  const [username, setUsername] = useState(user?.username ?? '')
  const [password, setPassword] = useState('')
  const [role, setRole] = useState<Role>(user?.role ?? 'friend')
  const [displayName, setDisplayName] = useState(user?.displayName ?? '')
  const [email, setEmail] = useState(user?.email ?? '')
  const [enabled, setEnabled] = useState(user?.enabled ?? true)
  const [approve, setApprove] = useState<Approve>(user?.autoApprove == null ? 'default' : user.autoApprove ? 'on' : 'off')
  const [custom, setCustom] = useState(user?.customLimits ?? false)
  const [movies, setMovies] = useState<string>(user?.movieRequestLimit?.toString() ?? '')
  const [series, setSeries] = useState<string>(user?.seriesRequestLimit?.toString() ?? '')
  const [days, setDays] = useState<string>((user?.requestLimitDays ?? 7).toString())
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<ApiError | Error | null>(null)
  const fields = error instanceof ApiError ? error.fieldErrors : {}

  const roles: Role[] = isOwner ? ['owner', 'admin', 'friend', 'requester'] : ['admin', 'friend', 'requester']
  // The role's default is only known for the stored role; for a new account or another role it is "Default".
  const defaultApprove = user && user.autoApprove == null && user.role === role ? (user.effectiveAutoApprove ? 'on' : 'off') : null
  const number = (value: string) => (value.trim() === '' ? null : Number(value))

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await onSave({
        username: user ? undefined : username.trim(),
        password: user ? undefined : password,
        role,
        displayName: displayName.trim() || null,
        email: email.trim() || null,
        enabled,
        autoApprove: approve === 'default' ? null : approve === 'on',
        movieRequestLimit: custom ? number(movies) : null,
        seriesRequestLimit: custom ? number(series) : null,
        requestLimitDays: custom ? number(days) : null,
      })
      onClose()
    } catch (err) {
      setError(err instanceof Error ? err : new Error(String(err)))
      setBusy(false)
    }
  }

  const err = (name: string) => fields[name]?.[0]

  return (
    <Modal title={user ? `Edit ${nameOf(user)}` : 'Add user'} onClose={onClose}>
      <form className={styles.form} onSubmit={(e) => void submit(e)} noValidate>
        {!user && (
          <div className={styles.grid2}>
            <label className={ui.field}>
              Username
              <input className={ui.input} value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="off" spellCheck={false} autoFocus />
              {err('username') ? <small className={styles.fieldError}>{err('username')}</small> : <small>Used to sign in.</small>}
            </label>
            <label className={ui.field}>
              Password
              <PasswordInput value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" />
              {err('password') ? <small className={styles.fieldError}>{err('password')}</small> : <small>At least 8 characters. Share it privately.</small>}
            </label>
          </div>
        )}

        <div className={styles.grid2}>
          <label className={ui.field}>
            Display name
            <input className={ui.input} value={displayName} onChange={(e) => setDisplayName(e.target.value)} placeholder="Optional" autoFocus={!!user} />
            {err('displayName') && <small className={styles.fieldError}>{err('displayName')}</small>}
          </label>
          <label className={ui.field}>
            E-mail
            <input className={ui.input} type="email" value={email} onChange={(e) => setEmail(e.target.value)} placeholder="Optional" spellCheck={false} />
            {err('email') && <small className={styles.fieldError}>{err('email')}</small>}
          </label>
        </div>

        <label className={ui.field}>
          Role
          <select className={ui.select} value={role} onChange={(e) => setRole(e.target.value as Role)} disabled={self}>
            {roles.map((r) => (
              <option key={r} value={r}>
                {roleNames[r]}
              </option>
            ))}
          </select>
          {err('role') ? (
            <small className={styles.fieldError}>{err('role')}</small>
          ) : (
            <small>{self ? 'You cannot change your own role.' : roleHelp[role]}</small>
          )}
        </label>

        {!self && (
          <div className={styles.switchRow}>
            <span>
              Enabled
              <small className={ui.muted}>Off: cannot sign in, open sessions end.</small>
            </span>
            <label className={ui.switch}>
              <input type="checkbox" role="switch" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} aria-label="Enabled" />
              <span aria-hidden="true" />
            </label>
          </div>
        )}
        {err('enabled') && <small className={styles.fieldError}>{err('enabled')}</small>}

        <div className={styles.group}>
          <div className={styles.groupHead}>
            <span>
              <b>Approve requests automatically</b>
              <small className={ui.muted}>Off: an admin approves each request.</small>
            </span>
            <div className={ui.seg} role="group" aria-label="Approve requests automatically">
              {(['default', 'on', 'off'] as const).map((a) => (
                <button key={a} type="button" aria-pressed={approve === a} onClick={() => setApprove(a)}>
                  {a === 'default' ? (defaultApprove ? `Default (${defaultApprove})` : 'Default') : a === 'on' ? 'Always' : 'Never'}
                </button>
              ))}
            </div>
          </div>
        </div>

        <div className={styles.group}>
          <div className={styles.groupHead}>
            <span>
              <b>Request limits</b>
              <small className={ui.muted}>{custom ? 'Empty: no limit for that type.' : 'Settings → Requests sets the defaults.'}</small>
            </span>
            <div className={ui.seg} role="group" aria-label="Request limits">
              <button type="button" aria-pressed={!custom} onClick={() => setCustom(false)}>
                Default
              </button>
              <button type="button" aria-pressed={custom} onClick={() => setCustom(true)}>
                Custom
              </button>
            </div>
          </div>
          {custom && (
            <div className={styles.grid3}>
              <label className={ui.field}>
                Movies
                <input className={`${ui.input} ${ui.num}`} type="number" min={0} value={movies} onChange={(e) => setMovies(e.target.value)} placeholder="No limit" />
                {err('movieRequestLimit') && <small className={styles.fieldError}>{err('movieRequestLimit')}</small>}
              </label>
              <label className={ui.field}>
                Series
                <input className={`${ui.input} ${ui.num}`} type="number" min={0} value={series} onChange={(e) => setSeries(e.target.value)} placeholder="No limit" />
                {err('seriesRequestLimit') && <small className={styles.fieldError}>{err('seriesRequestLimit')}</small>}
              </label>
              <label className={ui.field}>
                Per days
                <input className={`${ui.input} ${ui.num}`} type="number" min={1} max={365} value={days} onChange={(e) => setDays(e.target.value)} />
                {err('requestLimitDays') && <small className={styles.fieldError}>{err('requestLimitDays')}</small>}
              </label>
            </div>
          )}
        </div>

        {error && Object.keys(fields).length === 0 && (
          <p className={ui.errBox} role="alert">
            <Icon name="alert" size={16} />
            <span>{error.message}</span>
          </p>
        )}

        <div className={styles.dialogActions}>
          {actions && <div className={styles.dialogExtra}>{actions}</div>}
          <button type="button" className={ui.button} onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className={ui.primary} disabled={busy}>
            <Icon name={user ? 'check' : 'plus'} size={15} />
            {busy ? 'Saving…' : user ? 'Save' : 'Add user'}
          </button>
        </div>
      </form>
    </Modal>
  )
}

/** An admin sets a new password; the user's sessions end. */
export function PasswordDialog({ user, onSave, onClose }: { user: UserAccount; onSave: (password: string) => Promise<unknown>; onClose: () => void }) {
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await onSave(password)
      onClose()
    } catch (err) {
      setError(err instanceof ApiError ? (err.fieldErrors.password?.[0] ?? err.message) : String(err))
      setBusy(false)
    }
  }

  return (
    <Modal title="Reset password" onClose={onClose}>
      <form className={styles.form} onSubmit={(e) => void submit(e)} noValidate>
        <p className={styles.dialogText}>
          A new password for <b>{nameOf(user)}</b>. They are signed out everywhere and sign in again with it.
        </p>
        <label className={ui.field}>
          New password
          <PasswordInput value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" autoFocus />
          {error ? <small className={styles.fieldError}>{error}</small> : <small>At least 8 characters.</small>}
        </label>
        <div className={styles.dialogActions}>
          <button type="button" className={ui.button} onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className={ui.primary} disabled={busy || password.length === 0}>
            <Icon name="key" size={15} />
            {busy ? 'Saving…' : 'Set password'}
          </button>
        </div>
      </form>
    </Modal>
  )
}

/** A yes/no question before something that cannot be undone. */
export function ConfirmDialog({
  title,
  children,
  confirm,
  danger = false,
  onConfirm,
  onClose,
}: {
  title: string
  children: ReactNode
  confirm: string
  danger?: boolean
  onConfirm: () => Promise<unknown>
  onClose: () => void
}) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const run = async () => {
    setBusy(true)
    setError(null)
    try {
      await onConfirm()
      onClose()
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
      setBusy(false)
    }
  }

  return (
    <Modal title={title} onClose={onClose}>
      <div className={styles.form}>
        <div className={styles.dialogText}>{children}</div>
        {error && (
          <p className={ui.errBox} role="alert">
            <Icon name="alert" size={16} />
            <span>{error}</span>
          </p>
        )}
        <div className={styles.dialogActions}>
          <button type="button" className={ui.button} onClick={onClose}>
            Cancel
          </button>
          <button type="button" className={danger ? ui.dangerSolid : ui.primary} onClick={() => void run()} disabled={busy}>
            {busy ? 'Working…' : confirm}
          </button>
        </div>
      </div>
    </Modal>
  )
}
