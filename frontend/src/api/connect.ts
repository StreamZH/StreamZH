import { request } from './client'

export type ConnectKind = 'mediaServer' | 'notification'
export type ConnectTrigger = 'grab' | 'import' | 'upgrade' | 'rename' | 'downloadFailure' | 'healthIssue' | 'healthRestored'
export type ConnectFieldType = 'text' | 'url' | 'password' | 'number' | 'bool' | 'select' | 'multiSelect' | 'list' | 'textArea'
export type SettingValue = string | number | boolean | string[] | null

export interface ConnectField {
  /** Key in `settings` and of its validation error. */
  name: string
  label: string
  type: ConnectFieldType
  help: string | null
  default: SettingValue
  required: boolean
  /** Only shown with the Advanced switch on. */
  advanced: boolean
  options: { value: string; label: string }[] | null
  min: number | null
  max: number | null
  placeholder: string | null
  unit: string | null
}

export interface ConnectionTriggers {
  onGrab: boolean
  onImport: boolean
  onUpgrade: boolean
  onRename: boolean
  onDownloadFailure: boolean
  onHealthIssue: boolean
  onHealthRestored: boolean
  /** Health triggers also fire for warnings, not only errors. */
  includeHealthWarnings: boolean
}

/** One entry of the catalog (GET /connection/schema). */
export interface ConnectType {
  key: string
  name: string
  kind: ConnectKind
  description: string
  infoUrl: string | null
  /** Triggers this type can react to. */
  triggers: ConnectTrigger[]
  /** Triggers that are on for a new connection. */
  defaults: ConnectionTriggers
  fields: ConnectField[]
}

export interface ConnectionStatus {
  /** The last delivery failed and nothing worked since. */
  failing: boolean
  lastSuccess: string | null
  lastFailure: string | null
  lastError: string | null
}

export interface Connection extends ConnectionTriggers {
  id: number
  name: string
  implementation: string
  implementationName: string
  kind: ConnectKind
  enable: boolean
  /** Every field of the type; secrets come as "********" and keep the stored value when sent back. */
  settings: Record<string, SettingValue>
  status: ConnectionStatus
}

export const maskedSecret = '********'

export const triggerKeys: Record<ConnectTrigger, keyof ConnectionTriggers> = {
  grab: 'onGrab',
  import: 'onImport',
  upgrade: 'onUpgrade',
  rename: 'onRename',
  downloadFailure: 'onDownloadFailure',
  healthIssue: 'onHealthIssue',
  healthRestored: 'onHealthRestored',
}

export const connectKeys = {
  all: ['connections'] as const,
  list: ['connections', 'list'] as const,
  schema: ['connections', 'schema'] as const,
}

/** A new connection of this type: its default triggers and field defaults. */
export function newConnection(type: ConnectType): Connection {
  return {
    id: 0,
    name: type.name,
    implementation: type.key,
    implementationName: type.name,
    kind: type.kind,
    enable: true,
    ...type.defaults,
    settings: Object.fromEntries(type.fields.map((f) => [f.name, f.default ?? (f.type === 'bool' ? false : f.type === 'multiSelect' ? [] : f.type === 'number' ? null : '')])),
    status: { failing: false, lastSuccess: null, lastFailure: null, lastError: null },
  }
}

const body = (c: Connection) => {
  const { status: _status, implementationName: _name, kind: _kind, ...rest } = c
  return rest
}

export const connectApi = {
  list: () => request<Connection[]>('GET', '/connection'),
  schema: () => request<ConnectType[]>('GET', '/connection/schema'),
  create: (c: Connection) => request<Connection>('POST', '/connection', body(c)),
  update: (c: Connection) => request<Connection>('PUT', `/connection/${c.id}`, body(c)),
  remove: (id: number) => request<void>('DELETE', `/connection/${id}`),
  /** Nothing is saved; with the id of a saved connection, masked secrets use the stored ones. */
  test: (c: Connection) => request<{ message: string }>('POST', '/connection/test', body(c)),
}
