import { withBase } from '../lib/urlBase'

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
  role: 'Owner' | 'Admin' | 'Friend' | 'Requester'
}

export interface AuthState {
  setupRequired: boolean
  authenticated: boolean
  user: UserInfo | null
  instanceName: string
  /** False for sessions without a sign-in cookie (Basic, External, local network): no "Sign out". */
  canSignOut: boolean
  /** First-run setup from another device: the setup page asks for the one-time setup code. */
  setupCodeRequired: boolean
}

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

/** Path of an API resource below the URL base, for links and downloads: apiUrl('/system/logs/files/x'). */
export function apiUrl(path: string): string {
  return withBase(`/api/v1${path}`)
}

/**
 * Sent with every request of the web app (and the live connection): StreamZH refuses changing requests that cannot show
 * they come from its own pages, so other web pages cannot act in the user's name (cross-site request protection).
 */
export const appHeaders = { 'X-StreamZH-Request': '1' } as const

/** Fired when a request comes back 401, so the app can show the login page. */
export const unauthorizedEvent = 'streamzh:unauthorized'

export async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(apiUrl(path), {
    method,
    credentials: 'same-origin',
    headers: {
      ...appHeaders,
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

  // 204, or 202 Accepted without a body (restart, shutdown).
  const text = response.status === 204 ? '' : await response.text()
  return (text ? JSON.parse(text) : undefined) as T
}

export const api = {
  authState: () => request<AuthState>('GET', '/auth/state'),
  setup: (username: string, password: string, setupCode?: string) =>
    request<UserInfo>('POST', '/auth/setup', { username, password, ...(setupCode ? { setupCode } : {}) }),
  login: (username: string, password: string, rememberMe: boolean) =>
    request<UserInfo>('POST', '/auth/login', { username, password, rememberMe }),
  logout: () => request<void>('POST', '/auth/logout'),

  systemStatus: () => request<SystemStatus>('GET', '/system/status'),
}
