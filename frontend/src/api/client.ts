export interface SystemStatus {
  appName: string
  version: string
  runtime: string
  operatingSystem: string
  isDocker: boolean
  startedAt: string
  databaseSchemaVersion: number
}

export interface UserInfo {
  id: number
  username: string
  role: 'Owner' | 'Admin' | 'Friend'
}

export interface AuthState {
  setupRequired: boolean
  authenticated: boolean
  user: UserInfo | null
  instanceName: string
}

export interface GeneralSettings {
  instanceName: string
  logLevel: string
  apiKey: string
  dataDirectory: string
  logDirectory: string
}

export const logLevels = ['Trace', 'Debug', 'Information', 'Warning', 'Error'] as const

/** Error from the API, with field errors for validation problems (400). */
export class ApiError extends Error {
  readonly status: number
  readonly fieldErrors: Record<string, string[]>

  constructor(status: number, message: string, fieldErrors: Record<string, string[]> = {}) {
    super(message)
    this.status = status
    this.fieldErrors = fieldErrors
  }
}

/** Fired when a request comes back 401, so the app can show the login page. */
export const unauthorizedEvent = 'streamzh:unauthorized'

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(`/api/v1${path}`, {
    method,
    credentials: 'same-origin',
    headers: {
      Accept: 'application/json',
      ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
    },
    body: body !== undefined ? JSON.stringify(body) : undefined,
  })

  if (response.status === 401 && !path.startsWith('/auth/')) {
    window.dispatchEvent(new Event(unauthorizedEvent))
  }

  if (!response.ok) {
    let problem: { title?: string; detail?: string; errors?: Record<string, string[]> } = {}
    try {
      problem = await response.json()
    } catch {
      // no JSON body
    }
    const message =
      response.status === 429
        ? 'Too many attempts. Please wait a minute.'
        : (problem.detail ?? problem.title ?? `${response.status} ${response.statusText}`)
    throw new ApiError(response.status, message, problem.errors ?? {})
  }

  if (response.status === 204) {
    return undefined as T
  }

  return (await response.json()) as T
}

export const api = {
  authState: () => request<AuthState>('GET', '/auth/state'),
  setup: (username: string, password: string) => request<UserInfo>('POST', '/auth/setup', { username, password }),
  login: (username: string, password: string, rememberMe: boolean) =>
    request<UserInfo>('POST', '/auth/login', { username, password, rememberMe }),
  logout: () => request<void>('POST', '/auth/logout'),

  systemStatus: () => request<SystemStatus>('GET', '/system/status'),
  logs: (lines = 300) => request<{ lines: string[] }>('GET', `/system/logs?lines=${lines}`),

  generalSettings: () => request<GeneralSettings>('GET', '/settings/general'),
  saveGeneralSettings: (instanceName: string, logLevel: string) =>
    request<void>('PUT', '/settings/general', { instanceName, logLevel }),
  regenerateApiKey: () => request<{ apiKey: string }>('POST', '/settings/apikey'),
}
