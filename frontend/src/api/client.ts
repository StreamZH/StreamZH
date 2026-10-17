export interface SystemStatus {
  appName: string
  version: string
  runtime: string
  operatingSystem: string
  isDocker: boolean
  startedAt: string
  databaseSchemaVersion: number
}

async function get<T>(path: string): Promise<T> {
  const response = await fetch(`/api/v1${path}`, { headers: { Accept: 'application/json' } })
  if (!response.ok) {
    throw new Error(`${response.status} ${response.statusText}`)
  }
  return (await response.json()) as T
}

export const api = {
  systemStatus: () => get<SystemStatus>('/system/status'),
}
