import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router'
import {
  generalApi,
  generalKeys,
  logLevels,
  type AuthenticationMethod,
  type AuthenticationRequired,
  type CertificateValidation,
  type GeneralSettings,
  type GeneralSettingsChange,
  type ProxyType,
} from '../api/general'
import { authQueryKey, useAuth } from '../auth/auth'
import FolderPicker from '../components/FolderPicker'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import { formatDateTime } from '../lib/format'
import { useAdvanced } from '../lib/advancedMode'
import { RestartControls } from '../system/Restart'
import styles from './General.module.css'
import { NumberInput, Row, SaveBar, Section, Select, SettingsPage, Toggle } from './SettingsKit'
import { useSettingsForm } from './useSettingsForm'

/** Settings → General (like Radarr): host, security, API key, proxy, logging, backups, updates. */
export default function GeneralPage() {
  const settings = useQuery({ queryKey: generalKeys.general, queryFn: generalApi.general })

  return (
    <SettingsPage title="General" lead="Where StreamZH listens, who may sign in, proxy, logging, backups and updates.">
      {settings.isError && <p className={ui.error}>{settings.error.message}</p>}
      {settings.data?.restartRequired && <RestartBanner />}
      <HostSection />
      <SecuritySection />
      {settings.data && <ApiKeySection apiKey={settings.data.apiKey} />}
      <ProxySection />
      <LoggingSection />
      <BackupSection />
      <UpdatesSection />
      <Section id="analytics" title="Analytics">
        <p className={styles.text}>StreamZH sends no usage data, crash reports or analytics anywhere. It only talks to TMDB, your indexers and Usenet servers, and to GitHub when you check for updates.</p>
      </Section>
      {settings.data && (
        <Section id="storage" title="Storage">
          <Row label="Data folder" wide help="Database, settings, logs and keys. Set with --data or STREAMZH_DATA.">
            <code className={styles.path}>{settings.data.dataDirectory}</code>
          </Row>
          <Row label="Log folder" wide>
            <code className={styles.path}>{settings.data.logDirectory}</code>
          </Row>
        </Section>
      )}
    </SettingsPage>
  )
}

function RestartBanner() {
  return (
    <div className={styles.banner} role="status">
      <div className={styles.bannerText}>
        <strong>Restart required</strong>
        <small>Changed host settings (address, port, URL base or HTTPS) apply after a restart.</small>
      </div>
      <RestartControls compact />
    </div>
  )
}

/** One section of the page: loads the shared settings, edits only its own fields and saves only those. */
function useGeneralForm<T extends object>(name: string, select: (s: GeneralSettings) => T, toChange: (draft: T) => GeneralSettingsChange = (d) => d) {
  const queryClient = useQueryClient()
  return useSettingsForm<T>({
    queryKey: [...generalKeys.general, name],
    load: async () => select(await queryClient.fetchQuery({ queryKey: generalKeys.general, queryFn: generalApi.general, staleTime: 5_000 })),
    save: async (draft) => {
      const saved = await generalApi.saveGeneral(toChange(draft))
      queryClient.setQueryData(generalKeys.general, saved)
      void queryClient.invalidateQueries({ queryKey: authQueryKey })
      return select(saved)
    },
  })
}

function TextInput({
  id,
  value,
  onChange,
  placeholder,
  mono = false,
  type = 'text',
  autoComplete,
}: {
  id: string
  value: string
  onChange: (value: string) => void
  placeholder?: string
  mono?: boolean
  type?: 'text' | 'password' | 'url'
  autoComplete?: string
}) {
  return (
    <input
      id={id}
      className={`${ui.input} ${mono ? ui.mono : ''}`}
      type={type}
      value={value}
      placeholder={placeholder}
      autoComplete={autoComplete ?? 'off'}
      spellCheck={false}
      onChange={(e) => onChange(e.target.value)}
    />
  )
}

// ---- Host ----

interface HostDraft {
  instanceName: string
  bindAddress: string
  port: number | null
  urlBase: string
  applicationUrl: string
  enableSsl: boolean
  sslPort: number | null
  sslCertPath: string
  sslCertPassword: string | null
  sslCertPasswordSet: boolean
  hostOverride: string | null
  hostProblem: string | null
  listening: string[]
}

function HostSection() {
  const form = useGeneralForm<HostDraft>(
    'host',
    (s) => ({
      instanceName: s.instanceName,
      bindAddress: s.bindAddress,
      port: s.port,
      urlBase: s.urlBase,
      applicationUrl: s.applicationUrl,
      enableSsl: s.enableSsl,
      sslPort: s.sslPort,
      sslCertPath: s.sslCertPath,
      sslCertPassword: null,
      sslCertPasswordSet: s.sslCertPasswordSet,
      hostOverride: s.hostOverride,
      hostProblem: s.hostProblem,
      listening: s.listening,
    }),
    // Read-only fields are not sent; the password only when typed.
    (d) => ({
      instanceName: d.instanceName,
      bindAddress: d.bindAddress,
      port: d.port,
      urlBase: d.urlBase,
      applicationUrl: d.applicationUrl,
      enableSsl: d.enableSsl,
      sslPort: d.sslPort,
      sslCertPath: d.sslCertPath,
      ...(d.sslCertPassword !== null ? { sslCertPassword: d.sslCertPassword } : {}),
    }),
  )
  const advanced = useAdvanced()
  const v = form.values
  if (!v) return <Section id="host" title="Host">{form.query.isError ? <p className={ui.error}>{form.query.error.message}</p> : null}</Section>

  const hidden = advanced ? 0 : 2 + (v.enableSsl ? 3 : 0)
  return (
    <Section
      id="host"
      title="Host"
      description={
        v.hostOverride ? (
          <>
            Started with <code>{v.hostOverride}</code> (command line or ASPNETCORE_URLS), which wins over bind address, port and HTTPS here. The URL base still applies.
          </>
        ) : (
          <>Listening on {v.listening.join(', ')}. Address, port, URL base and HTTPS apply after a restart.</>
        )
      }
      hiddenCount={hidden}
      footer={<SaveBar dirty={form.dirty} saving={form.saving} saved={form.saved} error={form.formError} onSave={form.save} onReset={form.reset} />}
    >
      {v.hostProblem && <p className={ui.error}>{v.hostProblem}</p>}
      <Row label="Bind address" htmlFor="bindAddress" advanced error={form.error('bindAddress')} help="* for all network interfaces, or one IP address of this computer (127.0.0.1 = only this computer).">
        <TextInput id="bindAddress" value={v.bindAddress} onChange={(bindAddress) => form.set({ bindAddress })} mono />
      </Row>
      <Row label="Port" htmlFor="port" error={form.error('port')} help="Default 9797.">
        <NumberInput id="port" value={v.port} onChange={(port) => form.set({ port })} min={1} max={65535} />
      </Row>
      <Row
        label="URL base"
        htmlFor="urlBase"
        error={form.error('urlBase')}
        help={
          <>
            For a reverse proxy that serves StreamZH below a path, e.g. <code>/streamzh</code> for https://example.com/streamzh. The proxy must pass the path on
            unchanged. Empty = at the root.
          </>
        }
      >
        <TextInput id="urlBase" value={v.urlBase} onChange={(urlBase) => form.set({ urlBase })} placeholder="/streamzh" mono />
      </Row>
      <Row label="Instance name" htmlFor="instanceName" error={form.error('instanceName')} help="Shown in the browser tab and on the sign-in page.">
        <TextInput id="instanceName" value={v.instanceName} onChange={(instanceName) => form.set({ instanceName })} />
      </Row>
      <Row
        label="Application URL"
        htmlFor="applicationUrl"
        error={form.error('applicationUrl')}
        help="The address you open StreamZH with from outside, including the URL base. Used for links in notifications."
      >
        <TextInput id="applicationUrl" value={v.applicationUrl} onChange={(applicationUrl) => form.set({ applicationUrl })} placeholder="https://streamzh.example.com" type="url" mono />
      </Row>
      <Row label="HTTPS" advanced help="StreamZH serves HTTPS itself with your certificate. Behind a reverse proxy, leave it off and let the proxy do HTTPS.">
        <Toggle checked={v.enableSsl} onChange={(enableSsl) => form.set({ enableSsl })} />
      </Row>
      {v.enableSsl && (
        <>
          <Row label="HTTPS port" htmlFor="sslPort" advanced error={form.error('sslPort')} help="Default 9798. HTTP stays on the port above.">
            <NumberInput id="sslPort" value={v.sslPort} onChange={(sslPort) => form.set({ sslPort })} min={1} max={65535} />
          </Row>
          <Row label="Certificate" htmlFor="sslCertPath" advanced error={form.error('sslCertPath')} help="Path to a .pfx (PKCS #12) file with the private key.">
            <TextInput id="sslCertPath" value={v.sslCertPath} onChange={(sslCertPath) => form.set({ sslCertPath })} placeholder="/config/streamzh.pfx" mono />
          </Row>
          <Row label="Certificate password" htmlFor="sslCertPassword" advanced help={v.sslCertPasswordSet ? 'Saved. Type to replace it.' : 'Empty when the file has no password.'}>
            <TextInput
              id="sslCertPassword"
              type="password"
              autoComplete="new-password"
              value={v.sslCertPassword ?? ''}
              placeholder={v.sslCertPasswordSet ? '••••••••' : ''}
              onChange={(sslCertPassword) => form.set({ sslCertPassword })}
            />
          </Row>
        </>
      )}
    </Section>
  )
}

// ---- Security ----

interface SecurityDraft {
  authenticationMethod: AuthenticationMethod
  authenticationRequired: AuthenticationRequired
  certificateValidation: CertificateValidation
  authenticationMethodOverride: AuthenticationMethod | null
  allowedHostNames: string
  trustedProxies: string
  trustedLocalNetworks: string
  externalUserHeader: string
  runningInContainer: boolean
}

const methodOptions: { value: AuthenticationMethod; label: string }[] = [
  { value: 'forms', label: 'Forms (sign-in page)' },
  { value: 'basic', label: 'Basic (browser dialog)' },
  { value: 'external', label: 'External (reverse proxy)' },
]

const methodHelp: Record<AuthenticationMethod, string> = {
  forms: 'StreamZH’s own sign-in page. The default.',
  basic: 'The browser asks for user name and password (same accounts). Use it only over HTTPS; there is no sign-out.',
  external:
    'No sign-in in StreamZH: a reverse proxy in front of it (e.g. CloudZH single sign-on) must ask for it. Requests from the trusted proxies (none set: from local addresses) get full access.',
}

function SecuritySection() {
  const form = useGeneralForm<SecurityDraft>(
    'security',
    (s) => ({
      authenticationMethod: s.authenticationMethod,
      authenticationRequired: s.authenticationRequired,
      certificateValidation: s.certificateValidation,
      authenticationMethodOverride: s.authenticationMethodOverride,
      allowedHostNames: s.allowedHostNames,
      trustedProxies: s.trustedProxies,
      trustedLocalNetworks: s.trustedLocalNetworks,
      externalUserHeader: s.externalUserHeader,
      runningInContainer: s.runningInContainer,
    }),
    (d) => ({
      authenticationMethod: d.authenticationMethod,
      authenticationRequired: d.authenticationRequired,
      certificateValidation: d.certificateValidation,
      allowedHostNames: d.allowedHostNames,
      trustedProxies: d.trustedProxies,
      trustedLocalNetworks: d.trustedLocalNetworks,
      externalUserHeader: d.externalUserHeader,
    }),
  )
  const advanced = useAdvanced()
  const v = form.values
  if (!v) return null
  const external = v.authenticationMethod === 'external'
  const localBypass = !external && v.authenticationRequired === 'disabledForLocalAddresses'
  const signInFree = external || localBypass

  return (
    <Section
      id="security"
      title="Security"
      hiddenCount={advanced ? 0 : (v.certificateValidation === 'disabled' ? 0 : 1) + 1 + (localBypass ? 1 : 0) + (signInFree ? 1 : 0) + (external ? 1 : 0)}
      footer={<SaveBar dirty={form.dirty} saving={form.saving} saved={form.saved} error={form.formError} onSave={form.save} onReset={form.reset} />}
    >
      {v.authenticationMethodOverride && (
        <p className={styles.warn}>
          Started with <code>--auth-method {v.authenticationMethodOverride}</code>: that method is used until StreamZH starts without it.
        </p>
      )}
      <Row label="Authentication" htmlFor="authenticationMethod" error={form.error('authenticationMethod')} help={methodHelp[v.authenticationMethod]}>
        <Select id="authenticationMethod" value={v.authenticationMethod} onChange={(authenticationMethod) => form.set({ authenticationMethod })} options={methodOptions} />
        {v.authenticationMethod === 'external' && (
          <small className={styles.warn}>
            Locked out? Start StreamZH once with <code>--auth-method forms</code>.
          </small>
        )}
      </Row>
      {v.authenticationMethod !== 'external' && (
        <Row
          label="Authentication required"
          htmlFor="authenticationRequired"
          error={form.error('authenticationRequired')}
          help="Disabled for local addresses: this computer and the home network (10.x, 172.16–31.x, 192.168.x, IPv6 local) need no sign-in. Behind a reverse proxy the original address counts."
        >
          <Select
            id="authenticationRequired"
            value={v.authenticationRequired}
            onChange={(authenticationRequired) => form.set({ authenticationRequired })}
            options={[
              { value: 'enabled', label: 'Enabled' },
              { value: 'disabledForLocalAddresses', label: 'Disabled for local addresses' },
            ]}
          />
          {localBypass && (
            <small className={styles.warn}>
              Everyone who can reach StreamZH from these networks has full access, guests on your Wi-Fi and VPN users included.
              {v.runningInContainer && !v.trustedLocalNetworks.trim()
                ? ' StreamZH runs in a container: with published ports every visitor may come from the container network and count as local. Set Trusted local networks below or keep sign-in on.'
                : ''}
            </small>
          )}
        </Row>
      )}
      {localBypass && (
        <Row
          label="Trusted local networks"
          htmlFor="trustedLocalNetworks"
          advanced
          error={form.error('trustedLocalNetworks')}
          help="Only these addresses or networks count as local, e.g. 192.168.1.0/24. Empty: all private networks (10.x, 172.16–31.x, 192.168.x, IPv6 local) and this computer."
        >
          <TextInput
            id="trustedLocalNetworks"
            value={v.trustedLocalNetworks}
            onChange={(trustedLocalNetworks) => form.set({ trustedLocalNetworks })}
            placeholder="192.168.1.0/24, fd00::/8"
            mono
          />
        </Row>
      )}
      {signInFree && (
        <Row
          label="Allowed host names"
          htmlFor="allowedHostNames"
          advanced
          error={form.error('allowedHostNames')}
          help="Sign-in-free access is only given to IP addresses, local names (nas, *.local, *.lan), this computer's name and the application URL. Add other names you open StreamZH with, e.g. media.example.com or *.example.com."
        >
          <TextInput
            id="allowedHostNames"
            value={v.allowedHostNames}
            onChange={(allowedHostNames) => form.set({ allowedHostNames })}
            placeholder="media.example.com"
            mono
          />
        </Row>
      )}
      <Row
        label="Trusted proxies"
        htmlFor="trustedProxies"
        advanced
        error={form.error('trustedProxies')}
        help={
          external
            ? 'Addresses of your reverse proxies, e.g. 172.18.0.2 or 10.0.0.0/24. Only they may sign people in (External), and their client address and HTTPS headers count. Empty: any local address may sign in.'
            : 'Addresses of your reverse proxies, e.g. 172.18.0.2 or 10.0.0.0/24. Their X-Forwarded-For and X-Forwarded-Proto headers count: sign-in limits per real client, secure cookie behind HTTPS.'
        }
      >
        <TextInput id="trustedProxies" value={v.trustedProxies} onChange={(trustedProxies) => form.set({ trustedProxies })} placeholder="172.18.0.2" mono />
      </Row>
      {external && (
        <Row
          label="User header"
          htmlFor="externalUserHeader"
          advanced
          error={form.error('externalUserHeader')}
          help="Optional: the header your proxy sets with the signed-in user, e.g. Remote-User. Requests without it get the sign-in page."
        >
          <TextInput
            id="externalUserHeader"
            value={v.externalUserHeader}
            onChange={(externalUserHeader) => form.set({ externalUserHeader })}
            placeholder="Remote-User"
            mono
          />
        </Row>
      )}
      <Row
        label="Certificate validation"
        htmlFor="certificateValidation"
        advanced={v.certificateValidation !== 'disabled'}
        error={form.error('certificateValidation')}
        help="Checks HTTPS certificates of indexers, TMDB and other sites StreamZH calls. For your own servers with self-signed certificates choose “Disabled for local addresses”; “Disabled” lets anyone on the way read indexer API keys."
      >
        <Select
          id="certificateValidation"
          value={v.certificateValidation}
          onChange={(certificateValidation) => form.set({ certificateValidation })}
          options={[
            { value: 'enabled', label: 'Enabled' },
            { value: 'disabledForLocalAddresses', label: 'Disabled for local addresses' },
            { value: 'disabled', label: 'Disabled' },
          ]}
        />
        {v.certificateValidation === 'disabled' && (
          <small className={styles.warn} role="alert">
            Not recommended: API keys can be read.
          </small>
        )}
      </Row>
    </Section>
  )
}

// ---- API key ----

function ApiKeySection({ apiKey }: { apiKey: string }) {
  const { user } = useAuth()
  const queryClient = useQueryClient()
  const [visible, setVisible] = useState(false)
  const [copied, setCopied] = useState(false)
  const [confirming, setConfirming] = useState(false)

  const regenerate = useMutation({
    mutationFn: generalApi.regenerateApiKey,
    onSuccess: async () => {
      setConfirming(false)
      setVisible(true)
      await queryClient.invalidateQueries({ queryKey: generalKeys.general, exact: true })
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
    <Section
      id="api-key"
      title="API key"
      description={
        <>
          For CloudZH, scripts and other tools. Send it as the <code>X-Api-Key</code> header. Anyone with this key has full access.
        </>
      }
    >
      <Row label="API key" wide>
        <div className={styles.keyRow}>
          <code className={styles.key}>{visible ? apiKey : '•'.repeat(32)}</code>
          <button className={ui.button} type="button" onClick={() => setVisible((v) => !v)}>
            <Icon name={visible ? 'eyeoff' : 'eye'} size={15} />
            {visible ? 'Hide' : 'Show'}
          </button>
          <button className={ui.button} type="button" onClick={() => void copy()}>
            <Icon name={copied ? 'check' : 'copy'} size={15} />
            {copied ? 'Copied' : 'Copy'}
          </button>
        </div>
        {user?.role === 'Owner' && (
          <div className={styles.keyRow}>
            {confirming ? (
              <>
                <span className={styles.text}>Tools using the old key stop working.</span>
                <button className={ui.danger} type="button" onClick={() => regenerate.mutate()} disabled={regenerate.isPending}>
                  Yes, create new key
                </button>
                <button className={ui.button} type="button" onClick={() => setConfirming(false)}>
                  Cancel
                </button>
              </>
            ) : (
              <button className={ui.ghost} type="button" onClick={() => setConfirming(true)}>
                <Icon name="restart" size={15} />
                Create new key
              </button>
            )}
            {regenerate.error && <span className={ui.error}>{regenerate.error.message}</span>}
          </div>
        )}
      </Row>
    </Section>
  )
}

// ---- Proxy ----

interface ProxyDraft {
  proxyEnabled: boolean
  proxyType: ProxyType
  proxyHostname: string
  proxyPort: number | null
  proxyUsername: string
  proxyPassword: string | null
  proxyPasswordSet: boolean
  proxyBypassFilter: string
  proxyBypassLocalAddresses: boolean
}

function ProxySection() {
  const form = useGeneralForm<ProxyDraft>(
    'proxy',
    (s) => ({
      proxyEnabled: s.proxyEnabled,
      proxyType: s.proxyType,
      proxyHostname: s.proxyHostname,
      proxyPort: s.proxyPort,
      proxyUsername: s.proxyUsername,
      proxyPassword: null,
      proxyPasswordSet: s.proxyPasswordSet,
      proxyBypassFilter: s.proxyBypassFilter,
      proxyBypassLocalAddresses: s.proxyBypassLocalAddresses,
    }),
    ({ proxyPasswordSet: _set, proxyPassword, ...rest }) => ({ ...rest, ...(proxyPassword !== null ? { proxyPassword } : {}) }),
  )
  const v = form.values
  if (!v) return null

  return (
    <Section
      id="proxy"
      title="Proxy"
      advanced={!v.proxyEnabled && !form.hasError('proxyHostname', 'proxyPort', 'proxyType', 'proxyBypassFilter')}
      description="For every web request: indexers, TMDB, tool downloads and the update check. Usenet servers are always reached directly; the proxy carries web traffic only."
      footer={<SaveBar dirty={form.dirty} saving={form.saving} saved={form.saved} error={form.formError} onSave={form.save} onReset={form.reset} />}
    >
      <Row label="Use proxy" help="Off: StreamZH uses the system proxy (HTTP_PROXY, HTTPS_PROXY, Windows settings), if there is one.">
        <Toggle checked={v.proxyEnabled} onChange={(proxyEnabled) => form.set({ proxyEnabled })} />
      </Row>
      {v.proxyEnabled && (
        <>
          <Row label="Proxy type" htmlFor="proxyType" error={form.error('proxyType')}>
            <Select
              id="proxyType"
              value={v.proxyType}
              onChange={(proxyType) => form.set({ proxyType })}
              options={[
                { value: 'http', label: 'HTTP(S)' },
                { value: 'socks4', label: 'SOCKS4' },
                { value: 'socks5', label: 'SOCKS5' },
              ]}
            />
          </Row>
          <Row label="Hostname" htmlFor="proxyHostname" error={form.error('proxyHostname')} help="Host name or IP address of the proxy, without http://.">
            <TextInput id="proxyHostname" value={v.proxyHostname} onChange={(proxyHostname) => form.set({ proxyHostname })} placeholder="proxy.example.com" mono />
          </Row>
          <Row label="Port" htmlFor="proxyPort" error={form.error('proxyPort')}>
            <NumberInput id="proxyPort" value={v.proxyPort} onChange={(proxyPort) => form.set({ proxyPort })} min={1} max={65535} />
          </Row>
          <Row label="Username" htmlFor="proxyUsername" help="Only if the proxy needs a login.">
            <TextInput id="proxyUsername" value={v.proxyUsername} onChange={(proxyUsername) => form.set({ proxyUsername })} autoComplete="off" />
          </Row>
          <Row label="Password" htmlFor="proxyPassword" help={v.proxyPasswordSet ? 'Saved. Type to replace it; clear the field and save to remove it.' : undefined}>
            <TextInput
              id="proxyPassword"
              type="password"
              autoComplete="new-password"
              value={v.proxyPassword ?? ''}
              placeholder={v.proxyPasswordSet ? '••••••••' : ''}
              onChange={(proxyPassword) => form.set({ proxyPassword })}
            />
          </Row>
          <Row
            label="Ignored addresses"
            htmlFor="proxyBypassFilter"
            error={form.error('proxyBypassFilter')}
            help={
              <>
                Go direct, separated by commas. <code>*</code> as wildcard: <code>*.example.com, 192.168.1.*</code>
              </>
            }
          >
            <TextInput id="proxyBypassFilter" value={v.proxyBypassFilter} onChange={(proxyBypassFilter) => form.set({ proxyBypassFilter })} mono />
          </Row>
          <Row label="Bypass for local addresses" help="Your own servers (localhost, 192.168.x, names without a dot like “nas”) go direct.">
            <Toggle checked={v.proxyBypassLocalAddresses} onChange={(proxyBypassLocalAddresses) => form.set({ proxyBypassLocalAddresses })} />
          </Row>
        </>
      )}
    </Section>
  )
}

// ---- Logging ----

interface LoggingDraft {
  logLevel: string
  consoleLogLevel: string
  logSizeLimit: number | null
  logFileCount: number | null
}

const levelOptions = logLevels.map((level) => ({ value: level as string, label: level }))

function LoggingSection() {
  const form = useGeneralForm<LoggingDraft>('logging', (s) => ({
    logLevel: s.logLevel,
    consoleLogLevel: s.consoleLogLevel,
    logSizeLimit: s.logSizeLimit,
    logFileCount: s.logFileCount,
  }))
  const advanced = useAdvanced()
  const v = form.values
  if (!v) return null

  return (
    <Section
      id="logging"
      title="Logging"
      description={
        <>
          Read the log under <Link to="/system/logs">System → Logs</Link>. Changes apply at once.
        </>
      }
      hiddenCount={advanced ? 0 : 3}
      footer={<SaveBar dirty={form.dirty} saving={form.saving} saved={form.saved} error={form.formError} onSave={form.save} onReset={form.reset} />}
    >
      <Row label="Log level" htmlFor="logLevel" error={form.error('logLevel')} help="Debug and Trace help with troubleshooting, but write a lot.">
        <Select id="logLevel" value={v.logLevel} onChange={(logLevel) => form.set({ logLevel })} options={levelOptions} />
      </Row>
      <Row label="Log file size" htmlFor="logSizeLimit" advanced error={form.error('logSizeLimit')} help="A new file starts at this size.">
        <NumberInput id="logSizeLimit" value={v.logSizeLimit} onChange={(logSizeLimit) => form.set({ logSizeLimit })} unit="MB" min={1} max={100} />
      </Row>
      <Row label="Old log files" htmlFor="logFileCount" advanced error={form.error('logFileCount')} help="How many older files are kept next to the current one.">
        <NumberInput id="logFileCount" value={v.logFileCount} onChange={(logFileCount) => form.set({ logFileCount })} unit="files" min={1} max={50} />
      </Row>
      <Row label="Console log level" htmlFor="consoleLogLevel" advanced error={form.error('consoleLogLevel')} help="What the terminal or `docker logs` shows. Framework messages stay at Warning.">
        <Select id="consoleLogLevel" value={v.consoleLogLevel} onChange={(consoleLogLevel) => form.set({ consoleLogLevel })} options={levelOptions} />
      </Row>
    </Section>
  )
}

// ---- Backups ----

interface BackupDraft {
  backupFolder: string
  defaultBackupFolder: string
  backupInterval: number | null
  backupRetention: number | null
}

function BackupSection() {
  const form = useGeneralForm<BackupDraft>(
    'backups',
    (s) => ({ backupFolder: s.backupFolder, defaultBackupFolder: s.defaultBackupFolder, backupInterval: s.backupInterval, backupRetention: s.backupRetention }),
    (d) => ({ backupFolder: d.backupFolder === d.defaultBackupFolder ? '' : d.backupFolder, backupInterval: d.backupInterval, backupRetention: d.backupRetention }),
  )
  const advanced = useAdvanced()
  const [picking, setPicking] = useState(false)
  const v = form.values
  if (!v) return null

  return (
    <Section
      id="backups"
      title="Backups"
      description={
        <>
          Database and settings as a zip file. Make one now or restore under <Link to="/system/backup">System → Backup</Link>.
        </>
      }
      hiddenCount={advanced ? 0 : 1}
      footer={<SaveBar dirty={form.dirty} saving={form.saving} saved={form.saved} error={form.formError} onSave={form.save} onReset={form.reset} />}
    >
      <Row label="Folder" htmlFor="backupFolder" wide advanced error={form.error('backupFolder')} help="Another drive is safer. Scheduled backups go to “scheduled”, manual ones to “manual”.">
        <div className={styles.folderRow}>
          <TextInput id="backupFolder" value={v.backupFolder} onChange={(backupFolder) => form.set({ backupFolder })} mono />
          <button className={ui.button} type="button" onClick={() => setPicking(true)}>
            Browse…
          </button>
          {v.backupFolder !== v.defaultBackupFolder && (
            <button className={ui.button} type="button" onClick={() => form.set({ backupFolder: v.defaultBackupFolder })}>
              Default
            </button>
          )}
        </div>
      </Row>
      <Row label="Interval" htmlFor="backupInterval" error={form.error('backupInterval')} help="Days between scheduled backups. 0 = no scheduled backups.">
        <NumberInput id="backupInterval" value={v.backupInterval} onChange={(backupInterval) => form.set({ backupInterval })} unit="days" min={0} max={30} />
      </Row>
      <Row label="Retention" htmlFor="backupRetention" error={form.error('backupRetention')} help="Scheduled backups older than this are deleted. Manual backups stay.">
        <NumberInput id="backupRetention" value={v.backupRetention} onChange={(backupRetention) => form.set({ backupRetention })} unit="days" min={1} max={365} />
      </Row>
      {picking && (
        <FolderPicker
          initialPath={v.backupFolder}
          onClose={() => setPicking(false)}
          onSelect={(backupFolder) => {
            form.set({ backupFolder })
            setPicking(false)
          }}
        />
      )}
    </Section>
  )
}

// ---- Updates ----

function UpdatesSection() {
  const queryClient = useQueryClient()
  const updates = useQuery({ queryKey: generalKeys.updates, queryFn: generalApi.updates })
  const check = useMutation({ mutationFn: generalApi.checkUpdates, onSuccess: (data) => queryClient.setQueryData(generalKeys.updates, data) })
  const info = updates.data
  const last = info?.lastCheck

  return (
    <Section id="updates" title="Updates" description="StreamZH does not update itself yet. It can only tell you about a new version.">
      {info && (
        <>
          <Row label="Version">
            <span className={styles.value}>{info.version}</span>
          </Row>
          <Row label="Update mechanism" help={info.instructions}>
            <span className={styles.value}>{info.mechanism === 'docker' ? 'Docker image' : 'Manual'}</span>
          </Row>
          <Row label="Latest version" help={last?.checked ? `Checked ${formatDateTime(last.checked)}.` : 'Not checked since StreamZH started.'}>
            <div className={styles.keyRow}>
              <span className={styles.value}>
                {last?.latestVersion ? (
                  last.updateAvailable ? (
                    <>
                      <strong className={styles.ok}>{last.latestVersion} is available</strong>
                      {last.releaseUrl && (
                        <>
                          {' · '}
                          <a href={last.releaseUrl} target="_blank" rel="noreferrer noopener">
                            Release notes
                          </a>
                        </>
                      )}
                    </>
                  ) : (
                    <>You have the latest version.</>
                  )
                ) : (
                  (last?.message ?? '–')
                )}
              </span>
              <button className={ui.button} type="button" onClick={() => check.mutate()} disabled={check.isPending}>
                {check.isPending ? 'Checking…' : 'Check for updates'}
              </button>
            </div>
            {check.error && <small className={ui.error}>{check.error.message}</small>}
          </Row>
        </>
      )}
    </Section>
  )
}
