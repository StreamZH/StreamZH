import { request } from './client'

/** A Usenet provider account (GET /usenet/server). */
export interface UsenetServer {
  id: number
  name: string
  /** Host name only, e.g. news.example.com. */
  host: string
  port: number
  useSsl: boolean
  username: string
  /** Masked ("********") when a password is stored; send it back unchanged to keep it. */
  password: string
  /** Most connections StreamZH opens at once to this server. */
  connections: number
  /** 0 = primary; higher numbers are only asked for articles the lower ones do not have. */
  priority: number
  enabled: boolean
  /** 0 = unknown; older articles are not requested from this server. */
  retentionDays: number
}

export interface UsenetServerStatus {
  id: number
  name: string
  connectionsInUse: number
  connections: number
  /** False while the server is skipped after a refused login or when it could not be reached. */
  healthy: boolean
  lastError: string | null
  bytesToday: number
  /** While unhealthy: when it is tried again. */
  retryAt?: string | null
}

export interface UsenetStatus {
  servers: UsenetServerStatus[]
  speedBps: number
}

export interface UsenetTestResult {
  ok: boolean
  message: string
  latencyMs: number | null
}

export const maskedPassword = '********'

/** Defaults of a new server: SSL on 563, 20 connections, primary. */
export const newUsenetServer = (): UsenetServer => ({
  id: 0,
  name: '',
  host: '',
  port: 563,
  useSsl: true,
  username: '',
  password: '',
  connections: 20,
  priority: 0,
  enabled: true,
  retentionDays: 0,
})

export const usenetApi = {
  list: () => request<UsenetServer[]>('GET', '/usenet/server'),
  create: (server: UsenetServer) => request<UsenetServer>('POST', '/usenet/server', server),
  update: (server: UsenetServer) => request<UsenetServer>('PUT', `/usenet/server/${server.id}`, server),
  remove: (id: number) => request<void>('DELETE', `/usenet/server/${id}`),
  test: (server: UsenetServer) => request<UsenetTestResult>('POST', '/usenet/server/test', server),
  status: () => request<UsenetStatus>('GET', '/usenet/status'),
}

export const usenetKeys = {
  all: ['usenet'] as const,
  servers: ['usenet', 'servers'] as const,
  status: ['usenet', 'status'] as const,
}

/** "1.4 GB", "820 MB", "0 B". */
export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  const units = ['KB', 'MB', 'GB', 'TB']
  let value = bytes / 1024
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value >= 100 ? value.toFixed(0) : value.toFixed(1)} ${units[unit]}`
}
