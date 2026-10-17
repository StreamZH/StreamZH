import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { api, ApiError, logLevels, type GeneralSettings } from '../api/client'
import { authQueryKey, useAuth } from '../auth/auth'
import ui from '../components/ui.module.css'
import { useScrollToHash } from '../lib/useScrollToHash'
import { DownloadsSection, MetadataSection, RootFoldersSection } from './MediaSettings'
import page from './Page.module.css'
import styles from './Settings.module.css'

const settingsKey = ['settings-general']

export default function Settings() {
  const settings = useQuery({ queryKey: settingsKey, queryFn: api.generalSettings })
  useScrollToHash()

  return (
    <div className={page.page}>
      <header className={styles.header}>
        <div>
          <h1 className={page.title}>Settings</h1>
          <p className={page.lead}>Media folders, metadata, API access and logs.</p>
        </div>
        <nav className={styles.subnav} aria-label="More settings">
          <Link className={ui.button} to="/settings/profiles">
            Profiles
          </Link>
          <Link className={ui.button} to="/settings/release-test">
            Release test
          </Link>
        </nav>
      </header>

      <div id="media" className={page.page}>
        <RootFoldersSection />
        <DownloadsSection />
        <MetadataSection />
      </div>

      {settings.isError && <p className={page.error}>{settings.error.message}</p>}
      {settings.data && (
        <>
          <GeneralSection settings={settings.data} />
          <ApiKeySection apiKey={settings.data.apiKey} />
          <StorageSection settings={settings.data} />
        </>
      )}
      <LogSection />
    </div>
  )
}

function GeneralSection({ settings }: { settings: GeneralSettings }) {
  const queryClient = useQueryClient()
  const [instanceName, setInstanceName] = useState(settings.instanceName)
  const [logLevel, setLogLevel] = useState(settings.logLevel)
  const [saved, setSaved] = useState(false)

  const save = useMutation({
    mutationFn: () => api.saveGeneralSettings(instanceName, logLevel),
    onSuccess: async () => {
      setSaved(true)
      await queryClient.invalidateQueries({ queryKey: settingsKey })
      await queryClient.invalidateQueries({ queryKey: authQueryKey })
    },
  })

  useEffect(() => {
    if (!saved) return
    const timer = setTimeout(() => setSaved(false), 2500)
    return () => clearTimeout(timer)
  }, [saved])

  const fieldErrors = save.error instanceof ApiError ? save.error.fieldErrors : {}
  const dirty = instanceName !== settings.instanceName || logLevel !== settings.logLevel

  const submit = (event: FormEvent) => {
    event.preventDefault()
    save.mutate()
  }

  return (
    <form className={page.panel} onSubmit={submit}>
      <h2 className={page.panelTitle}>General</h2>
      <div className={styles.grid}>
        <label className={styles.field}>
          <span>Instance name</span>
          <input value={instanceName} maxLength={50} onChange={(e) => setInstanceName(e.target.value)} />
          <small>Shown in the browser tab and on the sign-in page.</small>
          {fieldErrors.instanceName && <small className={styles.error}>{fieldErrors.instanceName[0]}</small>}
        </label>
        <label className={styles.field}>
          <span>Log level</span>
          <select value={logLevel} onChange={(e) => setLogLevel(e.target.value)}>
            {logLevels.map((level) => (
              <option key={level} value={level}>
                {level}
              </option>
            ))}
          </select>
          <small>Debug and Trace help with troubleshooting, but write a lot.</small>
        </label>
      </div>
      <div className={styles.actions}>
        <button className={styles.primary} type="submit" disabled={!dirty || save.isPending}>
          {save.isPending ? 'Saving…' : 'Save'}
        </button>
        {saved && <span className={styles.ok}>Saved</span>}
        {save.error && Object.keys(fieldErrors).length === 0 && <span className={styles.error}>{save.error.message}</span>}
      </div>
    </form>
  )
}

function ApiKeySection({ apiKey }: { apiKey: string }) {
  const { user } = useAuth()
  const queryClient = useQueryClient()
  const [visible, setVisible] = useState(false)
  const [copied, setCopied] = useState(false)
  const [confirming, setConfirming] = useState(false)

  const regenerate = useMutation({
    mutationFn: api.regenerateApiKey,
    onSuccess: async () => {
      setConfirming(false)
      setVisible(true)
      await queryClient.invalidateQueries({ queryKey: settingsKey })
    },
  })

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(apiKey)
      setCopied(true)
      setTimeout(() => setCopied(false), 2000)
    } catch {
      setVisible(true)
    }
  }

  return (
    <section className={page.panel}>
      <h2 className={page.panelTitle}>API key</h2>
      <p className={styles.text}>
        For CloudZH, scripts and other tools. Send it as the <code>X-Api-Key</code> header. Anyone with this key has full access.
      </p>
      <div className={styles.keyRow}>
        <code className={styles.key}>{visible ? apiKey : '•'.repeat(32)}</code>
        <button className={styles.secondary} type="button" onClick={() => setVisible((v) => !v)}>
          {visible ? 'Hide' : 'Show'}
        </button>
        <button className={styles.secondary} type="button" onClick={() => void copy()}>
          {copied ? 'Copied' : 'Copy'}
        </button>
      </div>
      {user?.role === 'Owner' && (
        <div className={styles.actions}>
          {confirming ? (
            <>
              <span className={styles.text}>Tools using the old key stop working.</span>
              <button className={styles.danger} type="button" onClick={() => regenerate.mutate()} disabled={regenerate.isPending}>
                Yes, create new key
              </button>
              <button className={styles.secondary} type="button" onClick={() => setConfirming(false)}>
                Cancel
              </button>
            </>
          ) : (
            <button className={styles.secondary} type="button" onClick={() => setConfirming(true)}>
              Create new key
            </button>
          )}
          {regenerate.error && <span className={styles.error}>{regenerate.error.message}</span>}
        </div>
      )}
    </section>
  )
}

function StorageSection({ settings }: { settings: GeneralSettings }) {
  return (
    <section className={page.panel}>
      <h2 className={page.panelTitle}>Storage</h2>
      <dl className={styles.facts}>
        <dt>Data folder</dt>
        <dd>
          <code>{settings.dataDirectory}</code>
        </dd>
        <dt>Log folder</dt>
        <dd>
          <code>{settings.logDirectory}</code>
        </dd>
      </dl>
    </section>
  )
}

function LogSection() {
  const logs = useQuery({ queryKey: ['logs'], queryFn: () => api.logs(300), refetchInterval: 10_000 })

  return (
    <section className={page.panel}>
      <div className={styles.panelHead}>
        <h2 className={page.panelTitle}>Log</h2>
        <button className={styles.secondary} type="button" onClick={() => void logs.refetch()}>
          Refresh
        </button>
      </div>
      {logs.isError && <p className={styles.error}>{logs.error.message}</p>}
      <pre className={styles.log}>
        {(logs.data?.lines ?? []).length === 0 && <span className={styles.muted}>No entries yet.</span>}
        {[...(logs.data?.lines ?? [])].reverse().map((line, index) => {
          const level = line.split('|')[1] ?? ''
          return (
            <div key={index} className={styles[`level${level}`]}>
              {line}
            </div>
          )
        })}
      </pre>
    </section>
  )
}
