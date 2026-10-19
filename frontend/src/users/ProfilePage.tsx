import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState, type FormEvent, type ReactNode } from 'react'
import { ApiError } from '../api/client'
import { meApi, meKeys, type LimitUsage, type Me } from '../api/me'
import { initials, nameOf, roleNames } from '../api/users'
import PasswordInput from '../auth/PasswordInput'
import Icon from '../components/Icon'
import Loader from '../components/Loader'
import PageHeader from '../components/PageHeader'
import ui from '../components/ui.module.css'
import { formatDateTime, relativeTime } from '../lib/format'
import styles from './Users.module.css'

/** The signed-in user's own page (every role, incl. requesters): profile, password, request usage and Discord. */
export default function ProfilePage() {
  const me = useQuery({ queryKey: meKeys.me, queryFn: meApi.me })

  if (me.isPending) return <Loader text="Loading your profile" />
  if (me.isError) return <p className={ui.errBox}>{me.error.message}</p>

  const data = me.data
  return (
    <div className={styles.profile}>
      <PageHeader
        title="Profile"
        lead={
          <span className={styles.who}>
            <span className={`${styles.avatar} ${styles.avatarLg}`} aria-hidden="true">
              {initials(data)}
            </span>
            <span>
              <b>{nameOf(data)}</b>
              <small>
                @{data.username} · {roleNames[data.role]}
                {data.lastLogin && <> · signed in {relativeTime(data.lastLogin)}</>}
              </small>
            </span>
          </span>
        }
      />
      {!data.hasAccount ? (
        <p className={ui.infoBox}>
          <Icon name="info" size={16} />
          <span>This session has no user account (API key, local network or External sign-in), so there is nothing to edit here.</span>
        </p>
      ) : (
        <div className={styles.profileGrid}>
          <div className={styles.profileCol}>
            <AccountPanel me={data} />
            <PasswordPanel />
          </div>
          <div className={styles.profileCol}>
            <RequestsPanel me={data} />
            <DiscordPanel me={data} />
          </div>
        </div>
      )}
    </div>
  )
}

function Panel({ title, lead, children }: { title: string; lead?: ReactNode; children: ReactNode }) {
  return (
    <section className={`${ui.panel} ${styles.panel}`}>
      <div className={styles.panelBody}>
        <div>
          <h2 className={styles.panelTitle}>{title}</h2>
          {lead && <p className={styles.panelLead}>{lead}</p>}
        </div>
        {children}
      </div>
    </section>
  )
}

function Saved({ show }: { show: boolean }) {
  return show ? (
    <span className={styles.saved} role="status">
      <Icon name="check" size={15} /> Saved
    </span>
  ) : null
}

function AccountPanel({ me }: { me: Me }) {
  const queryClient = useQueryClient()
  const [displayName, setDisplayName] = useState(me.displayName ?? '')
  const [email, setEmail] = useState(me.email ?? '')
  const save = useMutation({
    mutationFn: () => meApi.save(displayName.trim() || null, email.trim() || null),
    onSuccess: (saved) => queryClient.setQueryData(meKeys.me, saved),
  })
  const fields = save.error instanceof ApiError ? save.error.fieldErrors : {}
  const dirty = displayName !== (me.displayName ?? '') || email !== (me.email ?? '')

  const submit = (e: FormEvent) => {
    e.preventDefault()
    save.mutate()
  }

  return (
    <Panel title="Account" lead="How you appear to admins and in Discord messages.">
      <form className={styles.form} onSubmit={submit} noValidate>
        <div className={styles.grid2}>
          <label className={ui.field}>
            Display name
            <input className={ui.input} value={displayName} onChange={(e) => setDisplayName(e.target.value)} placeholder={me.username} />
            {fields.displayName ? <small className={styles.fieldError}>{fields.displayName[0]}</small> : <small>Empty: your username.</small>}
          </label>
          <label className={ui.field}>
            E-mail
            <input className={ui.input} type="email" value={email} onChange={(e) => setEmail(e.target.value)} placeholder="Optional" spellCheck={false} />
            {fields.email && <small className={styles.fieldError}>{fields.email[0]}</small>}
          </label>
        </div>
        {save.error && Object.keys(fields).length === 0 && <p className={ui.errBox}>{save.error.message}</p>}
        <div className={styles.panelFoot}>
          <Saved show={save.isSuccess && !dirty} />
          <button type="submit" className={ui.primary} disabled={!dirty || save.isPending}>
            <Icon name="check" size={15} />
            {save.isPending ? 'Saving…' : 'Save'}
          </button>
        </div>
      </form>
    </Panel>
  )
}

function Meter({ label, item }: { label: string; item: LimitUsage }) {
  const share = item.limit ? Math.min(1, item.used / item.limit) : 0
  const full = item.limit != null && item.used >= item.limit
  return (
    <div className={styles.meter}>
      <div className={styles.meterHead}>
        <span>{label}</span>
        <span>{item.limit == null ? 'No limit' : `${Math.max(0, item.limit - item.used)} left`}</span>
      </div>
      <div className={styles.meterValue}>
        {item.used}
        <span>{item.limit == null ? `in ${item.days} days` : `of ${item.limit} in ${item.days} days`}</span>
      </div>
      {item.limit != null && (
        <div className={`${styles.bar} ${full ? styles.barFull : ''}`} role="progressbar" aria-valuenow={item.used} aria-valuemin={0} aria-valuemax={item.limit} aria-label={label}>
          <span style={{ width: `${share * 100}%` }} />
        </div>
      )}
    </div>
  )
}

function RequestsPanel({ me }: { me: Me }) {
  return (
    <Panel
      title="Requests"
      lead={me.autoApprove ? 'Your requests are approved automatically.' : 'An admin approves your requests before they are downloaded.'}
    >
      <div className={styles.meters}>
        <Meter label="Movies" item={me.usage.movies} />
        <Meter label="Series" item={me.usage.series} />
      </div>
    </Panel>
  )
}

/** "9:41" left until the code expires. */
function useCountdown(until: string | null): string | null {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    if (!until) return
    const timer = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(timer)
  }, [until])
  if (!until) return null
  const left = Math.max(0, Math.floor((new Date(until).getTime() - now) / 1000))
  return left === 0 ? null : `${Math.floor(left / 60)}:${String(left % 60).padStart(2, '0')}`
}

function DiscordPanel({ me }: { me: Me }) {
  const queryClient = useQueryClient()
  const refresh = () => queryClient.invalidateQueries({ queryKey: meKeys.me })
  const create = useMutation({ mutationFn: meApi.linkCode, onSuccess: () => refresh() })
  const unlink = useMutation({ mutationFn: meApi.unlinkDiscord, onSuccess: () => refresh() })
  const code = me.linkCode
  const left = useCountdown(code?.expires ?? null)
  const [copied, setCopied] = useState(false)

  // While a code is open, look for the link every few seconds (the bot stores it when /link is used).
  const open = code != null && !me.discord
  useEffect(() => {
    if (!open) return
    const timer = window.setInterval(() => void queryClient.invalidateQueries({ queryKey: meKeys.me }), 5000)
    return () => window.clearInterval(timer)
  }, [open, queryClient])

  if (me.discord) {
    return (
      <Panel title="Discord" lead="Request with the StreamZH bot on Discord as this user, and get a message when something is ready.">
        <dl className={styles.facts}>
          <dt>Linked account</dt>
          <dd>
            <span className={styles.discord}>
              <Icon name="chat" size={14} />
              {me.discord.name ?? me.discord.userId}
            </span>
          </dd>
          <dt>Since</dt>
          <dd title={formatDateTime(me.discord.linked)}>{relativeTime(me.discord.linked)}</dd>
        </dl>
        {unlink.error && <p className={ui.errBox}>{unlink.error.message}</p>}
        <div className={styles.panelFoot}>
          <button type="button" className={ui.danger} onClick={() => unlink.mutate()} disabled={unlink.isPending}>
            <Icon name="link" size={15} />
            {unlink.isPending ? 'Unlinking…' : 'Unlink Discord'}
          </button>
        </div>
      </Panel>
    )
  }

  return (
    <Panel title="Discord" lead="Link your Discord account to request with the StreamZH bot as this user and get a message when something is ready.">
      {code && left ? (
        <>
          <div className={styles.code}>
            <span className={styles.codeValue} aria-label={`Link code ${code.code.split('').join(' ')}`}>
              {code.code}
            </span>
            <button
              type="button"
              className={copied ? ui.button : ui.primary}
              onClick={() => {
                void navigator.clipboard?.writeText(`/link ${code.code}`).then(() => setCopied(true))
              }}
            >
              <Icon name={copied ? 'check' : 'copy'} size={15} />
              {copied ? 'Copied' : 'Copy command'}
            </button>
            <span className={`${ui.muted} ${ui.num}`}>Valid for {left}</span>
          </div>
          <ol className={styles.steps}>
            <li>Open Discord on the server with the StreamZH bot.</li>
            <li>
              Type <code>/link {code.code}</code> and send it.
            </li>
            <li>This page shows the linked account a few seconds later.</li>
          </ol>
        </>
      ) : (
        <p className={styles.dialogText}>You get a short code that works once, for 10 minutes.</p>
      )}
      {create.error && <p className={ui.errBox}>{create.error.message}</p>}
      <div className={styles.panelFoot}>
        <button type="button" className={code && left ? ui.button : ui.primary} onClick={() => (setCopied(false), create.mutate())} disabled={create.isPending}>
          <Icon name={code && left ? 'restart' : 'link'} size={15} />
          {create.isPending ? 'Creating…' : code && left ? 'New code' : 'Link Discord'}
        </button>
      </div>
    </Panel>
  )
}

function PasswordPanel() {
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [repeat, setRepeat] = useState('')
  const [mismatch, setMismatch] = useState(false)
  const change = useMutation({
    mutationFn: () => meApi.changePassword(current, next),
    onSuccess: () => {
      setCurrent('')
      setNext('')
      setRepeat('')
    },
  })
  const fields = change.error instanceof ApiError ? change.error.fieldErrors : {}

  const submit = (e: FormEvent) => {
    e.preventDefault()
    setMismatch(next !== repeat)
    if (next === repeat) change.mutate()
  }

  return (
    <Panel title="Password" lead="Other devices are signed out when you change it; this one stays signed in.">
      <form className={styles.form} onSubmit={submit} noValidate>
        <label className={ui.field}>
          Current password
          <PasswordInput value={current} onChange={(e) => setCurrent(e.target.value)} autoComplete="current-password" />
          {fields.currentPassword && <small className={styles.fieldError}>{fields.currentPassword[0]}</small>}
        </label>
        <div className={styles.grid2}>
          <label className={ui.field}>
            New password
            <PasswordInput value={next} onChange={(e) => setNext(e.target.value)} autoComplete="new-password" />
            {fields.newPassword ? <small className={styles.fieldError}>{fields.newPassword[0]}</small> : <small>At least 8 characters.</small>}
          </label>
          <label className={ui.field}>
            Repeat new password
            <PasswordInput value={repeat} onChange={(e) => setRepeat(e.target.value)} autoComplete="new-password" />
            {mismatch && <small className={styles.fieldError}>The passwords do not match.</small>}
          </label>
        </div>
        {change.error && Object.keys(fields).length === 0 && <p className={ui.errBox}>{change.error.message}</p>}
        <div className={styles.panelFoot}>
          <Saved show={change.isSuccess} />
          <button type="submit" className={ui.primary} disabled={change.isPending || !current || !next}>
            <Icon name="key" size={15} />
            {change.isPending ? 'Changing…' : 'Change password'}
          </button>
        </div>
      </form>
    </Panel>
  )
}
