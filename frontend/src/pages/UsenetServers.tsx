import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useId, useState, type FormEvent, type ReactNode } from 'react'
import { Link } from 'react-router'
import { ApiError } from '../api/client'
import {
  formatBytes,
  maskedPassword,
  newUsenetServer,
  usenetApi,
  usenetKeys,
  type UsenetServer,
  type UsenetServerStatus,
} from '../api/usenet'
import { useAuth } from '../auth/auth'
import ui from '../components/ui.module.css'
import { Pill } from '../indexers/Chips'
import Dialog from '../indexers/Dialog'
import form from '../indexers/Form.module.css'
import page from './Page.module.css'
import styles from './UsenetServers.module.css'

const sslPort = 563
const plainPort = 119

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
    <div className={page.page}>
      <header>
        <Link className={styles.back} to="/settings">
          ← Settings
        </Link>
        <h1 className={page.title}>Usenet servers</h1>
        <p className={page.lead}>
          Where StreamZH downloads from. The primary server is asked first; backup servers, such as a block account, fill in what it is missing.
        </p>
      </header>
      <ServerList canEdit={canEdit} />
    </div>
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
    <section className={page.panel}>
      <div className={styles.panelHead}>
        <div>
          <h2 className={page.panelTitle}>Servers</h2>
          {list.length > 0 && (
            <p className={styles.summary}>
              {list.length} server{list.length === 1 ? '' : 's'} · {connections} connections
              {status.data && status.data.speedBps > 0 && <> · {formatBytes(status.data.speedBps)}/s now</>}
            </p>
          )}
        </div>
        {canEdit && list.length > 0 && (
          <button className={ui.primary} type="button" onClick={add}>
            Add server
          </button>
        )}
      </div>
      {servers.isError && <p className={ui.error}>{servers.error.message}</p>}

      {servers.isSuccess && list.length === 0 && (
        <div className={styles.empty}>
          <p>
            Add the news server of your Usenet provider, with the user name and password from your account. Most providers use{' '}
            <strong>port 563 with SSL</strong>.
          </p>
          {canEdit ? (
            <button className={ui.primary} type="button" onClick={add}>
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
    </section>
  )
}

function formatTime(iso: string) {
  return new Date(iso).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
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
  const state = !s.enabled ? styles.disabled : failing ? styles.failing : styles.enabled
  const inUse = status?.connectionsInUse ?? 0

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
          {!s.enabled && <Pill tone="off">Disabled</Pill>}
          {failing && <Pill tone="error">{status?.retryAt ? `Skipped until ${formatTime(status.retryAt)}` : 'Failing'}</Pill>}
          {s.enabled && !failing && inUse > 0 && <Pill tone="ok">Downloading</Pill>}
        </div>
        <div className={styles.hostLine}>
          <span className={styles.host}>
            {s.host}:{s.port}
          </span>
          <span className={s.useSsl ? styles.ssl : styles.noSsl}>{s.useSsl ? 'SSL' : 'No SSL'}</span>
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
            className={ui.button}
            type="button"
            onClick={(e) => {
              e.stopPropagation()
              onEdit()
            }}
          >
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

function Field({ id, label, help, error, children }: { id?: string; label: string; help?: ReactNode; error?: string; children: ReactNode }) {
  return (
    <div className={`${form.row} ${error ? form.invalid : ''}`}>
      {id ? (
        <label className={form.label} htmlFor={id}>
          {label}
        </label>
      ) : (
        <span className={form.label}>{label}</span>
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

  const save = useMutation({ mutationFn: () => (isNew ? usenetApi.create(draft) : usenetApi.update(draft)), onSuccess: done })
  const remove = useMutation({ mutationFn: () => usenetApi.remove(server.id), onSuccess: done })
  const test = useMutation({ mutationFn: () => usenetApi.test(draft) })

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
    <Dialog title={title} subtitle={isNew ? 'News server of your Usenet provider' : `${server.host}:${server.port}`} onClose={onClose} footer={footer}>
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

          <Field
            id={id('priority')}
            label="Priority"
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
        </fieldset>
      </form>
    </Dialog>
  )
}
