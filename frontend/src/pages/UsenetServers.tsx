import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useId, useState, type FormEvent, type ReactNode } from 'react'
import { Link } from 'react-router'
import { ApiError } from '../api/client'
import {
  formatBytes,
  maskedPassword,
  newUsenetServer,
  quotaUsage,
  usenetApi,
  usenetKeys,
  type CertificateCheck,
  type FailureAction,
  type UsenetServer,
  type UsenetServerStatus,
} from '../api/usenet'
import { useAuth } from '../auth/auth'
import Icon from '../components/Icon'
import { Skeleton } from '../components/Loader'
import ui from '../components/ui.module.css'
import { Pill } from '../indexers/Chips'
import Dialog from '../indexers/Dialog'
import form from '../indexers/Form.module.css'
import { Advanced, AdvancedHint, AdvancedToggle } from '../lib/Advanced'
import { Section, SettingsPage } from '../settings/SettingsKit'
import { advancedLabel, setAdvanced } from '../lib/advancedMode'
import { formatDate, formatTime } from '../lib/format'
import page from './Page.module.css'
import styles from './UsenetServers.module.css'

const sslPort = 563
const plainPort = 119

/** Fields only shown in advanced mode. A save or test error on one of them switches advanced mode on. */
const advancedFields = [
  'priority',
  'retentionDays',
  'timeoutSeconds',
  'certificateCheck',
  'articlesPerRequest',
  'onFailure',
  'expiresOn',
  'quotaGb',
  'notes',
]

const showHiddenErrors = (error: Error) => {
  if (error instanceof ApiError && Object.keys(error.fieldErrors).some((name) => advancedFields.includes(name))) setAdvanced(true)
}

function useCanEdit() {
  const { user } = useAuth()
  return user?.role === 'Owner' || user?.role === 'Admin'
}

function useServers() {
  return useQuery({ queryKey: usenetKeys.servers, queryFn: usenetApi.list })
}

/** Settings → Usenet servers: the provider accounts the built-in downloader uses. */
export default function UsenetServers() {
  const canEdit = useCanEdit()

  return (
    <SettingsPage
      title="Usenet servers"
      lead="Where StreamZH downloads from. The primary server is asked first; backup servers, such as a block account, fill in what it is missing."
    >
      <ServerList canEdit={canEdit} />
    </SettingsPage>
  )
}

/** Card on the Settings page with a short summary and a link to the servers page. */
export function UsenetServersCard() {
  const servers = useServers()
  const list = servers.data ?? []
  const enabled = list.filter((s) => s.enabled)
  const primary = [...enabled].sort((a, b) => a.priority - b.priority)[0]

  return (
    <section className={`${page.panel} ${styles.card}`}>
      <div className={styles.cardText}>
        <h2 className={page.panelTitle}>Usenet servers</h2>
        {servers.isError && <p className={ui.error}>{servers.error.message}</p>}
        {servers.isSuccess &&
          (list.length === 0 ? (
            <p className={styles.cardSummary}>No server yet. The built-in downloader needs the news server of your Usenet provider.</p>
          ) : (
            <p className={styles.cardSummary}>
              {list.length} server{list.length === 1 ? '' : 's'} · {enabled.reduce((sum, s) => sum + s.connections, 0)} connections
              {primary && (
                <>
                  {' '}
                  · primary <strong>{primary.name}</strong>
                </>
              )}
            </p>
          ))}
      </div>
      <Link className={ui.button} to="/settings/usenet">
        {servers.isSuccess && list.length === 0 ? 'Add a server' : 'Manage servers'}
      </Link>
    </section>
  )
}

type View = { kind: 'none' } | { kind: 'form'; server: UsenetServer }

function ServerList({ canEdit }: { canEdit: boolean }) {
  const servers = useServers()
  const status = useQuery({ queryKey: usenetKeys.status, queryFn: usenetApi.status, refetchInterval: 5_000 })
  const [view, setView] = useState<View>({ kind: 'none' })
  const close = () => setView({ kind: 'none' })

  const list = servers.data ?? []
  const statusById = new Map((status.data?.servers ?? []).map((s) => [s.id, s]))
  const connections = list.filter((s) => s.enabled).reduce((sum, s) => sum + s.connections, 0)
  const add = () => setView({ kind: 'form', server: newUsenetServer() })

  return (
    <Section
      id="servers"
      title="Servers"
      description={
        list.length > 0 && (
          <span className={ui.num}>
            {list.length} server{list.length === 1 ? '' : 's'} · {connections} connections
            {status.data && status.data.speedBps > 0 && <> · {formatBytes(status.data.speedBps)}/s now</>}
          </span>
        )
      }
      actions={
        canEdit &&
        list.length > 0 && (
          <button className={`${ui.primary} ${ui.sm}`} type="button" onClick={add}>
            <Icon name="plus" size={14} />
            Add server
          </button>
        )
      }
    >
      {servers.isPending && <Skeleton rows={2} />}
      {servers.isError && <p className={ui.error}>{servers.error.message}</p>}

      {servers.isSuccess && list.length === 0 && (
        <div className={styles.empty}>
          <span className={styles.emptyIcon} aria-hidden="true">
            <Icon name="server" size={20} />
          </span>
          <p>
            Add the news server of your Usenet provider, with the user name and password from your account. Most providers use{' '}
            <strong>port 563 with SSL</strong>.
          </p>
          {canEdit ? (
            <button className={ui.primary} type="button" onClick={add}>
              <Icon name="plus" size={15} />
              Add server
            </button>
          ) : (
            <p className={ui.muted}>An admin can add servers.</p>
          )}
        </div>
      )}

      {list.length > 0 && (
        <>
          <div className={styles.head} aria-hidden="true">
            <span>Server</span>
            <span>Connections</span>
            <span>Today</span>
            <span>Retention</span>
            <span />
          </div>
          <ul className={styles.list}>
            {list.map((s) => (
              <ServerRow
                key={s.id}
                server={s}
                status={statusById.get(s.id) ?? null}
                canEdit={canEdit}
                onEdit={() => setView({ kind: 'form', server: s })}
              />
            ))}
          </ul>
        </>
      )}

      {view.kind === 'form' && <ServerForm key={view.server.id} server={view.server} canEdit={canEdit} onClose={close} />}
    </Section>
  )
}

function ServerRow({
  server: s,
  status,
  canEdit,
  onEdit,
}: {
  server: UsenetServer
  status: UsenetServerStatus | null
  canEdit: boolean
  onEdit: () => void
}) {
  const failing = s.enabled && status != null && !status.healthy
  const quotaFull = s.enabled && status?.quotaExhausted === true
  const state = !s.enabled ? styles.disabled : failing || quotaFull ? styles.failing : styles.enabled
  const inUse = status?.connectionsInUse ?? 0
  const quotaUsed = status?.quotaUsedBytes ?? s.quotaUsedBytes

  return (
    <li className={`${styles.row} ${state}`} onClick={canEdit ? onEdit : undefined}>
      <div className={styles.main}>
        <div className={styles.nameLine}>
          {canEdit ? (
            <button
              type="button"
              className={styles.name}
              onClick={(e) => {
                e.stopPropagation()
                onEdit()
              }}
            >
              {s.name}
            </button>
          ) : (
            <span className={styles.name}>{s.name}</span>
          )}
          {s.priority === 0 ? (
            <Pill tone="info" title="Priority 0: asked first">
              Primary
            </Pill>
          ) : (
            <Pill tone="off" title={`Priority ${s.priority}: only asked for articles the servers before it do not have`}>
              Backup
            </Pill>
          )}
          {s.onFailure === 'pause' && (
            <Pill tone="warn" title="Required: downloads pause while this server cannot be used">
              Required
            </Pill>
          )}
          {s.onFailure === 'ignore' && (
            <Pill tone="off" title="Optional: skipped for longer on errors, which are not shown in the status window">
              Optional
            </Pill>
          )}
          {!s.enabled && <Pill tone="off">Disabled</Pill>}
          {failing && <Pill tone="error">{status?.retryAt ? `Skipped until ${formatTime(status.retryAt)}` : 'Failing'}</Pill>}
          {quotaFull && <Pill tone="error">Quota used up</Pill>}
          {s.enabled && !failing && !quotaFull && inUse > 0 && <Pill tone="ok">Downloading</Pill>}
        </div>
        <div className={styles.hostLine}>
          <span className={styles.host}>
            {s.host}:{s.port}
          </span>
          <span className={s.useSsl ? styles.ssl : styles.noSsl}>{s.useSsl ? 'SSL' : 'No SSL'}</span>
          {s.quotaGb != null && <span className={styles.quota}>{quotaUsage(quotaUsed, s.quotaGb)}</span>}
        </div>
      </div>
      <div className={styles.cell}>
        <span className={styles.cellLabel}>Connections</span>
        <span className={styles.connections}>
          {s.enabled && inUse > 0 ? (
            <>
              <strong>{inUse}</strong> / {s.connections} in use
            </>
          ) : (
            <>{s.connections}</>
          )}
          {s.enabled && (
            <span className={styles.meter} aria-hidden="true">
              <span style={{ width: `${Math.min(100, Math.round((inUse / Math.max(1, s.connections)) * 100))}%` }} />
            </span>
          )}
        </span>
      </div>
      <div className={styles.cell}>
        <span className={styles.cellLabel}>Today</span>
        <span>{status ? formatBytes(status.bytesToday) : '–'}</span>
      </div>
      <div className={styles.cell}>
        <span className={styles.cellLabel}>Retention</span>
        <span>{s.retentionDays > 0 ? `${s.retentionDays.toLocaleString()} days` : 'Unknown'}</span>
      </div>
      <div className={styles.actions}>
        {canEdit && (
          <button
            className={`${ui.ghost} ${ui.sm}`}
            type="button"
            onClick={(e) => {
              e.stopPropagation()
              onEdit()
            }}
          >
            <Icon name="edit" size={14} />
            Edit
          </button>
        )}
      </div>
      {status?.lastError && s.enabled && (
        <p className={styles.lastError}>
          {status.healthy ? 'Last error' : 'Error'}: {status.lastError}
        </p>
      )}
    </li>
  )
}

function Field({
  id,
  label,
  advanced = false,
  help,
  error,
  children,
}: {
  id?: string
  label: string
  /** An expert setting: the label stands out while advanced mode is on. */
  advanced?: boolean
  help?: ReactNode
  error?: string
  children: ReactNode
}) {
  const labelClass = `${form.label} ${advanced ? advancedLabel : ''}`
  return (
    <div className={`${form.row} ${error ? form.invalid : ''}`}>
      {id ? (
        <label className={labelClass} htmlFor={id}>
          {label}
        </label>
      ) : (
        <span className={labelClass}>{label}</span>
      )}
      <div className={form.control}>
        {children}
        {help && <small className={form.help}>{help}</small>}
        {error && <small className={form.fieldError}>{error}</small>}
      </div>
    </div>
  )
}

function Switch({ checked, onChange, on, off }: { checked: boolean; onChange: (checked: boolean) => void; on: string; off: string }) {
  return (
    <label className={form.switch}>
      <input type="checkbox" role="switch" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      <span className={form.track} aria-hidden="true" />
      <span>{checked ? on : off}</span>
    </label>
  )
}

const numberValue = (value: number) => (Number.isNaN(value) ? '' : value)

const certificateHelp: Record<CertificateCheck, string> = {
  strict: 'The certificate must be valid, trusted and issued for this host. Use this unless the server has a self-signed certificate.',
  minimal: 'A self-signed, untrusted or expired certificate is accepted, but it must be issued for this host. The connection is still encrypted.',
  off: 'Any certificate is accepted. Unsafe: someone between you and the server could read your login and downloads. Only for a server you trust.',
}

const failureHelp: Record<FailureAction, string> = {
  skip: 'When the server fails (connection, login, quota used up), StreamZH skips it for 5 minutes and the other servers carry on. Right for most servers.',
  pause:
    'Required: when the server fails, downloads pause until it works again instead of skipping it. For your main or only provider, so articles are not wrongly counted as missing and downloads do not fail while it is down.',
  ignore:
    'Optional: an unreliable extra server. When it fails it is skipped for 30 minutes, and its errors do not show up in the status window.',
}

/** Add or edit a server; single column with help per field, like the indexer form. */
function ServerForm({ server, canEdit, onClose }: { server: UsenetServer; canEdit: boolean; onClose: () => void }) {
  const queryClient = useQueryClient()
  const formId = useId()
  const [draft, setDraft] = useState<UsenetServer>(server)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const isNew = server.id === 0

  const done = async () => {
    await queryClient.invalidateQueries({ queryKey: usenetKeys.all })
    onClose()
  }

  const save = useMutation({
    mutationFn: () => (isNew ? usenetApi.create(draft) : usenetApi.update(draft)),
    onSuccess: done,
    onError: showHiddenErrors,
  })
  const remove = useMutation({ mutationFn: () => usenetApi.remove(server.id), onSuccess: done })
  const test = useMutation({ mutationFn: () => usenetApi.test(draft), onError: showHiddenErrors })

  // Live quota counter (the list polls the status); the reset starts it again from zero.
  const status = useQuery({ queryKey: usenetKeys.status, queryFn: usenetApi.status, enabled: !isNew })
  const live = status.data?.servers.find((s) => s.id === server.id)
  const [counter, setCounter] = useState({ used: server.quotaUsedBytes, since: server.quotaSince })
  const quotaUsed = live?.quotaUsedBytes ?? counter.used
  const resetQuota = useMutation({
    mutationFn: () => usenetApi.resetQuota(server.id),
    onSuccess: async (saved) => {
      setCounter({ used: saved.quotaUsedBytes, since: saved.quotaSince })
      await queryClient.invalidateQueries({ queryKey: usenetKeys.all })
    },
  })

  // Fields changed since the last save or test: their old error no longer applies and is hidden.
  const [edited, setEdited] = useState<Set<string>>(new Set())
  const update = (change: Partial<UsenetServer>) => {
    setDraft((d) => ({ ...d, ...change }))
    setEdited((e) => new Set([...e, ...Object.keys(change)]))
  }

  const saveErrors = save.error instanceof ApiError ? save.error.fieldErrors : {}
  const testErrors = test.error instanceof ApiError ? test.error.fieldErrors : {}
  const fieldError = (name: string) => (edited.has(name) ? undefined : (saveErrors[name] ?? testErrors[name])?.[0])
  const openErrors = [...Object.keys(saveErrors), ...Object.keys(testErrors)].some((name) => !edited.has(name))

  const submit = (event: FormEvent) => {
    event.preventDefault()
    setEdited(new Set())
    test.reset()
    save.mutate()
  }

  // Switching SSL moves the port along when it is one of the usual two.
  const toggleSsl = (useSsl: boolean) => {
    const port = useSsl && draft.port === plainPort ? sslPort : !useSsl && draft.port === sslPort ? plainPort : draft.port
    update({ useSsl, port })
  }

  const id = (name: string) => `${formId}-${name}`
  const title = isNew ? 'Add Usenet server' : canEdit ? `Edit ${server.name}` : server.name

  const footer = canEdit ? (
    confirmDelete ? (
      <>
        <span className={form.confirm}>Delete {server.name}? Downloads use the remaining servers.</span>
        <span className={form.spacer} />
        <button className={ui.button} type="button" onClick={() => setConfirmDelete(false)}>
          Keep
        </button>
        <button className={ui.danger} type="button" onClick={() => remove.mutate()} disabled={remove.isPending}>
          {remove.isPending ? 'Deleting…' : 'Yes, delete'}
        </button>
      </>
    ) : (
      <>
        <div className={`${form.testResult} ${styles.messages}`} aria-live="polite">
          {test.isPending && <span className={ui.muted}>Connecting and logging in…</span>}
          {test.isSuccess && <span className={ui.ok}>{test.data.message}</span>}
          {test.error && Object.keys(testErrors).length === 0 && <span className={ui.error}>{test.error.message}</span>}
          {save.error && Object.keys(saveErrors).length === 0 && <span className={ui.error}>{save.error.message}</span>}
          {openErrors && <span className={ui.error}>Check the highlighted fields.</span>}
          {remove.error && <span className={ui.error}>{remove.error.message}</span>}
          {resetQuota.error && <span className={ui.error}>{resetQuota.error.message}</span>}
        </div>
        {!isNew && (
          <button className={`${ui.button} ${form.delete}`} type="button" onClick={() => setConfirmDelete(true)}>
            Delete
          </button>
        )}
        <span className={form.spacer} />
        <button
          className={ui.button}
          type="button"
          onClick={() => {
            setEdited(new Set())
            save.reset()
            test.mutate()
          }}
          disabled={test.isPending}
        >
          {test.isPending ? 'Testing…' : 'Test'}
        </button>
        <button className={ui.button} type="button" onClick={onClose}>
          Cancel
        </button>
        <button className={ui.primary} type="submit" form={formId} disabled={save.isPending}>
          {save.isPending ? 'Saving…' : 'Save'}
        </button>
      </>
    )
  ) : (
    <>
      <span className={form.spacer} />
      <button className={ui.button} type="button" onClick={onClose}>
        Close
      </button>
    </>
  )

  return (
    <Dialog
      title={title}
      subtitle={isNew ? 'News server of your Usenet provider' : `${server.host}:${server.port}`}
      onClose={onClose}
      footer={footer}
      headerAction={<AdvancedToggle />}
    >
      <form id={formId} className={form.form} onSubmit={submit} noValidate>
        <fieldset className={form.fieldset} disabled={!canEdit}>
          <Field id={id('name')} label="Name" help="Shown in the status and the log, e.g. the provider's name." error={fieldError('name')}>
            <input
              id={id('name')}
              className={ui.input}
              value={draft.name}
              maxLength={100}
              placeholder="My provider"
              onChange={(e) => update({ name: e.target.value })}
              autoFocus
            />
          </Field>

          <Field label="Enable" help="Off: StreamZH never connects to this server." error={fieldError('enabled')}>
            <Switch checked={draft.enabled} onChange={(enabled) => update({ enabled })} on="Enabled" off="Disabled" />
          </Field>

          <Field
            id={id('host')}
            label="Host"
            error={fieldError('host')}
            help="The news server address from your provider, without nntp:// and without the port."
          >
            <input
              id={id('host')}
              className={`${ui.input} ${ui.mono}`}
              autoComplete="off"
              autoCapitalize="off"
              spellCheck={false}
              inputMode="url"
              placeholder="news.example.com"
              value={draft.host}
              onChange={(e) => update({ host: e.target.value })}
            />
          </Field>

          <Field label="SSL" help="Encrypts the connection, so your internet provider cannot see what you download. Recommended." error={fieldError('useSsl')}>
            <Switch checked={draft.useSsl} onChange={toggleSsl} on="SSL on" off="SSL off" />
          </Field>

          <Field id={id('port')} label="Port" error={fieldError('port')} help={`Usually ${sslPort} with SSL and ${plainPort} without. Some providers also offer 443 or 80.`}>
            <input
              id={id('port')}
              className={`${ui.input} ${form.number}`}
              type="number"
              min={1}
              max={65535}
              inputMode="numeric"
              value={numberValue(draft.port)}
              onChange={(e) => update({ port: e.target.valueAsNumber })}
            />
          </Field>

          <Field id={id('username')} label="User name" error={fieldError('username')} help="From your provider account. Leave empty if the server needs no login.">
            <input
              id={id('username')}
              className={ui.input}
              autoComplete="off"
              autoCapitalize="off"
              spellCheck={false}
              value={draft.username}
              onChange={(e) => update({ username: e.target.value })}
            />
          </Field>

          <Field
            id={id('password')}
            label="Password"
            error={fieldError('password')}
            help={draft.password === maskedPassword ? 'Saved. Type a new password to replace it.' : 'Kept in the StreamZH data folder and never shown again.'}
          >
            <input
              id={id('password')}
              className={`${ui.input} ${ui.mono}`}
              type="password"
              autoComplete="new-password"
              spellCheck={false}
              value={draft.password}
              onFocus={(e) => draft.password === maskedPassword && e.target.select()}
              onChange={(e) => update({ password: e.target.value })}
            />
          </Field>

          <Field
            id={id('connections')}
            label="Connections"
            error={fieldError('connections')}
            help="How many your plan allows (often 20 to 50). More only help up to the speed of your internet line."
          >
            <input
              id={id('connections')}
              className={`${ui.input} ${form.number}`}
              type="number"
              min={1}
              max={100}
              inputMode="numeric"
              value={numberValue(draft.connections)}
              onChange={(e) => update({ connections: e.target.valueAsNumber })}
            />
          </Field>

          <Advanced>
            <Field
              id={id('priority')}
              label="Priority"
              advanced
              error={fieldError('priority')}
              help="0 is the primary server, asked first. Backup servers (1 to 99) are only asked for articles the servers before them do not have."
            >
              <input
                id={id('priority')}
                className={`${ui.input} ${form.number}`}
                type="number"
                min={0}
                max={99}
                inputMode="numeric"
                value={numberValue(draft.priority)}
                onChange={(e) => update({ priority: e.target.valueAsNumber })}
              />
            </Field>

            <Field
              id={id('retentionDays')}
              label="Retention"
              advanced
              error={fieldError('retentionDays')}
              help="Days the server keeps articles. Older posts are not requested from it. 0 if you do not know."
            >
              <div className={styles.withUnit}>
                <input
                  id={id('retentionDays')}
                  className={`${ui.input} ${styles.unitInput}`}
                  type="number"
                  min={0}
                  inputMode="numeric"
                  value={numberValue(draft.retentionDays)}
                  onChange={(e) => update({ retentionDays: e.target.valueAsNumber })}
                />
                <span className={ui.muted}>days</span>
              </div>
            </Field>

            <Field
              id={id('timeoutSeconds')}
              label="Timeout"
              advanced
              error={fieldError('timeoutSeconds')}
              help="How long StreamZH waits for an answer before it gives up on a connection and tries again (10 to 300 seconds)."
            >
              <div className={styles.withUnit}>
                <input
                  id={id('timeoutSeconds')}
                  className={`${ui.input} ${styles.unitInput}`}
                  type="number"
                  min={10}
                  max={300}
                  inputMode="numeric"
                  value={numberValue(draft.timeoutSeconds)}
                  onChange={(e) => update({ timeoutSeconds: e.target.valueAsNumber })}
                />
                <span className={ui.muted}>seconds</span>
              </div>
            </Field>

            {draft.useSsl && (
              <Field
                id={id('certificateCheck')}
                label="Certificate check"
                advanced
                error={fieldError('certificateCheck')}
                help={certificateHelp[draft.certificateCheck]}
              >
                <select
                  id={id('certificateCheck')}
                  className={`${ui.select} ${form.number}`}
                  value={draft.certificateCheck}
                  onChange={(e) => update({ certificateCheck: e.target.value as CertificateCheck })}
                >
                  <option value="strict">Strict (recommended)</option>
                  <option value="minimal">Minimal</option>
                  <option value="off">Off (unsafe)</option>
                </select>
              </Field>
            )}

            <Field
              id={id('articlesPerRequest')}
              label="Articles per request"
              advanced
              error={fieldError('articlesPerRequest')}
              help="How many articles StreamZH asks for on one connection before the first one arrives (pipelining, 1 to 20). 2 can help when the server is far away; 1 is the safe choice."
            >
              <input
                id={id('articlesPerRequest')}
                className={`${ui.input} ${form.number}`}
                type="number"
                min={1}
                max={20}
                inputMode="numeric"
                value={numberValue(draft.articlesPerRequest)}
                onChange={(e) => update({ articlesPerRequest: e.target.valueAsNumber })}
              />
            </Field>

            <Field id={id('onFailure')} label="On failure" advanced error={fieldError('onFailure')} help={failureHelp[draft.onFailure]}>
              <select
                id={id('onFailure')}
                className={ui.select}
                value={draft.onFailure}
                onChange={(e) => update({ onFailure: e.target.value as FailureAction })}
              >
                <option value="skip">Skip the server for a few minutes</option>
                <option value="pause">Pause downloads (required server)</option>
                <option value="ignore">Ignore it for longer (optional server)</option>
              </select>
            </Field>

            <Field
              id={id('expiresOn')}
              label="Account expires"
              advanced
              error={fieldError('expiresOn')}
              help="When your provider account ends. The status window reminds you 5 days before and up to 5 days after."
            >
              <div className={form.inline}>
                <input
                  id={id('expiresOn')}
                  className={`${ui.input} ${form.date}`}
                  type="date"
                  value={draft.expiresOn ?? ''}
                  onChange={(e) => update({ expiresOn: e.target.value || null })}
                />
                {draft.expiresOn && (
                  <button type="button" className={form.inlineLink} onClick={() => update({ expiresOn: null })}>
                    Clear
                  </button>
                )}
              </div>
            </Field>

            <Field
              id={id('quotaGb')}
              label="Quota"
              advanced
              error={fieldError('quotaGb')}
              help="For block accounts and plans with a download limit. Counting starts when you set it; once it is used up the server is skipped. Empty for no quota."
            >
              <div className={styles.withUnit}>
                <input
                  id={id('quotaGb')}
                  className={`${ui.input} ${styles.unitInput}`}
                  type="number"
                  min={0}
                  step="any"
                  inputMode="decimal"
                  placeholder="No quota"
                  value={draft.quotaGb ?? ''}
                  onChange={(e) => update({ quotaGb: e.target.value === '' || Number.isNaN(e.target.valueAsNumber) ? null : e.target.valueAsNumber })}
                />
                <span className={ui.muted}>GB</span>
              </div>
              {!isNew && server.quotaGb != null && (
                <div className={`${styles.usage} ${quotaUsed >= server.quotaGb * 1e9 ? styles.usageFull : ''}`}>
                  <span>
                    {quotaUsage(quotaUsed, server.quotaGb)}
                    {counter.since && <> since {formatDate(counter.since)}</>}
                  </span>
                  {canEdit && (
                    <button type="button" className={ui.button} onClick={() => resetQuota.mutate()} disabled={resetQuota.isPending}>
                      {resetQuota.isPending ? 'Resetting…' : 'Reset counter'}
                    </button>
                  )}
                </div>
              )}
            </Field>

            <Field id={id('notes')} label="Notes" advanced error={fieldError('notes')} help="Only for you, e.g. when you bought the account or what it costs.">
              <textarea
                id={id('notes')}
                className={`${ui.input} ${styles.notes}`}
                maxLength={2000}
                rows={3}
                value={draft.notes}
                onChange={(e) => update({ notes: e.target.value })}
              />
            </Field>
          </Advanced>
        </fieldset>
        <AdvancedHint count={draft.useSsl ? advancedFields.length : advancedFields.length - 1} />
      </form>
    </Dialog>
  )
}
