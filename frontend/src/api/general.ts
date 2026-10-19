import type { OngoingNotice } from './status'
import { ApiError, apiUrl, appHeaders, request, unauthorizedEvent } from './client'

// Settings → General and UI, and the System pages (Status, Tasks, Backup, Logs).

export const logLevels = ['Trace', 'Debug', 'Information', 'Warning', 'Error'] as const
export type AuthenticationMethod = 'forms' | 'basic' | 'external'
export type AuthenticationRequired = 'enabled' | 'disabledForLocalAddresses'
export type CertificateValidation = 'enabled' | 'disabledForLocalAddresses' | 'disabled'
export type ProxyType = 'http' | 'socks4' | 'socks5'

/** GET /settings/general. Passwords only as "is set". */
export interface GeneralSettings {
  instanceName: string
  bindAddress: string
  port: number
  urlBase: string
  applicationUrl: string
  enableSsl: boolean
  sslPort: number
  sslCertPath: string
  sslCertPasswordSet: boolean
  authenticationMethod: AuthenticationMethod
  /** Set by --auth-method on the command line; wins over the stored method. */
  authenticationMethodOverride: AuthenticationMethod | null
  authenticationRequired: AuthenticationRequired
  certificateValidation: CertificateValidation
  apiKey: string
  proxyEnabled: boolean
  proxyType: ProxyType
  proxyHostname: string
  proxyPort: number
  proxyUsername: string
  proxyPasswordSet: boolean
  proxyBypassFilter: string
  proxyBypassLocalAddresses: boolean
  logLevel: string
  consoleLogLevel: string
  logSizeLimit: number
  logFileCount: number
  backupFolder: string
  defaultBackupFolder: string
  backupInterval: number
  backupRetention: number
  dataDirectory: string
  logDirectory: string
  restartRequired: boolean
  /** Addresses from --urls or ASPNETCORE_URLS; then bind address, port and SSL are not used. */
  hostOverride: string | null
  listening: string[]
  hostProblem: string | null
  /** Security (advanced, 0.12): host names for the sign-in-free modes (besides IPs, local names and the application URL). */
  allowedHostNames: string
  /** Reverse proxies whose forwarding headers count; External sign-in only from them (empty: from local addresses). */
  trustedProxies: string
  /** Networks of "Disabled for local addresses"; empty = the private networks. */
  trustedLocalNetworks: string
  /** External: the header the proxy must send with the signed-in user; empty = not required. */
  externalUserHeader: string
  runningInContainer: boolean
}

/** PUT /settings/general: only the fields to change. Passwords: null keeps, '' clears. */
export type GeneralSettingsChange = Partial<{
  [K in keyof GeneralSettings]: GeneralSettings[K] | null
}> & { sslCertPassword?: string | null; proxyPassword?: string | null }

export interface UiSettings {
  shortDateFormat: string
  longDateFormat: string
  timeFormat: string
  showRelativeDates: boolean
  shortDateFormats: string[]
  longDateFormats: string[]
  timeFormats: string[]
  /** Calendar: "monday" (default) or "sunday". */
  firstDayOfWeek: 'monday' | 'sunday'
  firstDaysOfWeek: ('monday' | 'sunday')[]
}

export interface UpdateCheck {
  checked: string | null
  latestVersion: string | null
  published: string | null
  releaseUrl: string | null
  updateAvailable: boolean
  message: string | null
}

export interface UpdateInfo {
  version: string
  mechanism: 'docker' | 'manual'
  instructions: string
  lastCheck: UpdateCheck
}

export interface DiskSpaceEntry {
  label: string
  path: string
  drive: string | null
  freeBytes: number | null
  totalBytes: number | null
  problem: string | null
}

export interface SystemInfo {
  appName: string
  version: string
  runtime: string
  operatingSystem: string
  architecture: string
  runtimeMode: 'docker' | 'windowsService' | 'systemd' | 'console'
  dataDirectory: string
  databaseFile: string
  databaseSize: number
  databaseSchemaVersion: number
  startedAt: string
  uptimeSeconds: number
  listening: string[]
  urlBase: string
  hostOverride: string | null
  diskSpace: DiskSpaceEntry[]
  health: OngoingNotice[]
  restore: { success: boolean; message: string; previousFolder: string | null } | null
  license: string
}

export interface ScheduledTask {
  key: string
  name: string
  /** Null when the task is switched off. */
  intervalMinutes: number | null
  lastRun: string | null
  nextRun: string | null
  lastDurationSeconds: number | null
  lastSuccess: boolean | null
  lastError: string | null
  running: boolean
  queued: boolean
}

export interface BackupInfo {
  name: string
  type: 'scheduled' | 'manual'
  size: number
  time: string
}

export interface BackupList {
  backups: BackupInfo[]
  pendingRestore: { source: string; staged: string; version: string | null } | null
  folder: string
  intervalDays: number
  retentionDays: number
}

export interface LogEntry {
  time: string
  level: string
  logger: string
  message: string
  details: string | null
}

export interface LogFile {
  name: string
  size: number
  lastWrite: string
  current: boolean
}

export const generalKeys = {
  general: ['settings', 'general'] as const,
  ui: ['settings', 'ui'] as const,
  updates: ['system', 'updates'] as const,
  info: ['system', 'info'] as const,
  tasks: ['system', 'tasks'] as const,
  backups: ['system', 'backups'] as const,
  logs: (level: string, search: string) => ['system', 'logs', level, search] as const,
  logFiles: ['system', 'log-files'] as const,
}

export const generalApi = {
  general: () => request<GeneralSettings>('GET', '/settings/general'),
  saveGeneral: (change: GeneralSettingsChange) => request<GeneralSettings>('PUT', '/settings/general', change),
  regenerateApiKey: () => request<{ apiKey: string }>('POST', '/settings/apikey'),

  ui: () => request<UiSettings>('GET', '/settings/ui'),
  saveUi: (change: Partial<Pick<UiSettings, 'shortDateFormat' | 'longDateFormat' | 'timeFormat' | 'showRelativeDates' | 'firstDayOfWeek'>>) =>
    request<UiSettings>('PUT', '/settings/ui', change),

  restart: () => request<void>('POST', '/system/restart'),
  shutdown: () => request<void>('POST', '/system/shutdown'),
  updates: () => request<UpdateInfo>('GET', '/system/updates'),
  checkUpdates: () => request<UpdateInfo>('POST', '/system/updates/check'),

  info: () => request<SystemInfo>('GET', '/system/info'),
  tasks: () => request<ScheduledTask[]>('GET', '/system/tasks'),
  runTask: (key: string) => request<{ queued: boolean }>('POST', `/system/tasks/${encodeURIComponent(key)}/run`),

  backups: () => request<BackupList>('GET', '/system/backup'),
  backupNow: () => request<BackupInfo>('POST', '/system/backup'),
  deleteBackup: (b: BackupInfo) => request<void>('DELETE', `/system/backup/${b.type}/${encodeURIComponent(b.name)}`),
  restoreBackup: (b: BackupInfo) => request<BackupList['pendingRestore']>('POST', `/system/backup/${b.type}/${encodeURIComponent(b.name)}/restore`),
  cancelRestore: () => request<void>('DELETE', '/system/backup/restore'),
  backupUrl: (b: BackupInfo) => apiUrl(`/system/backup/${b.type}/${encodeURIComponent(b.name)}`),
  /** The zip goes as the raw request body (no form), like a download in reverse. */
  uploadRestore: async (file: File) => {
    const response = await fetch(apiUrl(`/system/backup/restore?name=${encodeURIComponent(file.name)}`), {
      method: 'POST',
      credentials: 'same-origin',
      headers: { ...appHeaders, 'Content-Type': 'application/zip', Accept: 'application/json' },
      body: file,
    })
    if (response.status === 401) window.dispatchEvent(new Event(unauthorizedEvent))
    if (!response.ok) {
      let problem: { title?: string; detail?: string; errors?: Record<string, string[]> } = {}
      try {
        problem = await response.json()
      } catch {
        // no JSON body
      }
      const fieldErrors = problem.errors ?? {}
      throw new ApiError(response.status, fieldErrors.file?.[0] ?? problem.detail ?? problem.title ?? `${response.status} ${response.statusText}`, fieldErrors)
    }
    return (await response.json()) as BackupList['pendingRestore']
  },

  logs: (lines: number, level: string, search: string) =>
    request<{ lines: string[]; entries: LogEntry[] }>(
      'GET',
      `/system/logs?lines=${lines}${level ? `&level=${encodeURIComponent(level)}` : ''}${search ? `&search=${encodeURIComponent(search)}` : ''}`,
    ),
  logFiles: () => request<LogFile[]>('GET', '/system/logs/files'),
  deleteLogFile: (name: string) => request<void>('DELETE', `/system/logs/files/${encodeURIComponent(name)}`),
  logFileUrl: (name: string) => apiUrl(`/system/logs/files/${encodeURIComponent(name)}`),
}
