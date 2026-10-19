import { request } from './client'

// 0.11 «Torrent»: download clients (qBittorrent, Transmission, Deluge, the built-in torrent engine), remote path
// mappings and the built-in engine's settings. The resources are normalised on load, so the pages work with the
// Radarr-style shape (fields with values) as well as with a settings dictionary.

export type DownloadProtocol = 'usenet' | 'torrent'

export type ClientFieldType = 'text' | 'number' | 'password' | 'checkbox' | 'select' | 'path'

export type ClientValue = string | number | boolean | null

export interface ClientFieldOption {
  value: string | number
  label: string
}

/** One implementation setting as /downloadclient/schema describes it. */
export interface ClientField {
  name: string
  label: string
  type: ClientFieldType
  options?: ClientFieldOption[]
  helpText?: string | null
  advanced?: boolean
  required?: boolean
  /** Default for a new client. */
  value?: ClientValue
  unit?: string | null
  placeholder?: string | null
}

/** One implementation in the add dialog. */
export interface ClientImplementation {
  implementation: string
  name: string
  protocol: DownloadProtocol
  description: string | null
  infoUrl: string | null
  fields: ClientField[]
}

/** Optional health of a client, when the server sends it (the tracker's last poll). */
export interface ClientStatus {
  healthy: boolean
  message: string | null
  version: string | null
  checkedAt: string | null
}

export interface DownloadClient {
  id: number
  name: string
  implementation: string
  implementationName: string
  protocol: DownloadProtocol
  enable: boolean
  /** 1 to 50, 1 is the highest: the enabled client with the lowest number gets new torrents. */
  priority: number
  /** After import: remove the torrent once the seed goals are met. */
  removeCompleted: boolean
  /** The implementation's settings (host, port, useSsl, urlBase, username, password, category …). Secrets come back masked. */
  settings: Record<string, ClientValue>
  status: ClientStatus | null
}

export interface ClientTestResult {
  ok: boolean
  message: string
}

export interface RemotePathMapping {
  id: number
  /** Host of the download client, as in its settings. */
  host: string
  /** Path as the client reports it. */
  remotePath: string
  /** The same folder as StreamZH sees it. */
  localPath: string
}

/** Settings of the built-in torrent engine (GET/PUT /settings/torrentengine). Unknown keys are kept and shown as expert settings. */
export type TorrentEngineSettings = Record<string, ClientValue>

/** Live state of the built-in engine (GET /torrentengine/status). */
export interface EngineStatus {
  wanted: boolean
  running: boolean
  listenPort: number
  /** "listening", "notFree" (another program has the port), "idle" (nothing to transfer) or "stopped". */
  portState: string
  startError: string | null
  torrents: number
  active: number
  /** Bytes per second. */
  downloadSpeed: number
  uploadSpeed: number
  engineVersion: string
  problems: { hash: string; name: string; kind: string; message: string }[]
}

export const maskedSecret = '********'

export const builtInImplementation = 'StreamZH'

/** Known implementations in picker order, with a short description for servers that send none. */
export const knownImplementations: { implementation: string; name: string; description: string; infoUrl: string | null }[] = [
  {
    implementation: builtInImplementation,
    name: 'StreamZH built-in',
    description: 'Download torrents inside StreamZH. Nothing else to install.',
    infoUrl: null,
  },
  {
    implementation: 'qBittorrent',
    name: 'qBittorrent',
    description: 'Through the qBittorrent Web UI (version 4.1 or newer).',
    infoUrl: 'https://www.qbittorrent.org/',
  },
  {
    implementation: 'Transmission',
    name: 'Transmission',
    description: 'Through the Transmission RPC interface (transmission-daemon or the app with remote access on).',
    infoUrl: 'https://transmissionbt.com/',
  },
  {
    implementation: 'Deluge',
    name: 'Deluge',
    description: 'Through the Deluge Web UI, with the Label plugin for categories.',
    infoUrl: 'https://deluge-torrent.org/',
  },
]

type RawField = Partial<ClientField> & { name: string; default?: ClientValue; selectOptions?: ClientFieldOption[] }
type RawImplementation = Partial<Omit<ClientImplementation, 'fields'>> & {
  implementation: string
  implementationName?: string
  infoLink?: string | null
  fields?: RawField[]
  settings?: Record<string, ClientValue>
}
/** The server's health of a client: { ok, error, field, since, lastContact } (or this app's own shape). */
type RawStatus = Partial<ClientStatus> & { ok?: boolean; error?: string | null; field?: string | null; since?: string | null; lastContact?: string | null }
type RawClient = Partial<Omit<DownloadClient, 'settings' | 'status'>> & {
  id: number
  status?: RawStatus | null
  settings?: Record<string, ClientValue>
  fields?: { name: string; value?: ClientValue }[]
  removeCompletedDownloads?: boolean
}

const fieldTypes: ClientFieldType[] = ['text', 'number', 'password', 'checkbox', 'select', 'path']

function normalizeField(f: RawField): ClientField {
  const type = fieldTypes.includes(f.type as ClientFieldType) ? (f.type as ClientFieldType) : 'text'
  return {
    name: f.name,
    label: f.label ?? f.name,
    type,
    options: f.options ?? f.selectOptions,
    helpText: f.helpText ?? null,
    advanced: f.advanced ?? false,
    required: f.required ?? false,
    value: f.value !== undefined ? f.value : (f.default ?? null),
    unit: f.unit ?? null,
    placeholder: f.placeholder ?? null,
  }
}

function knownOf(implementation: string) {
  return knownImplementations.find((k) => k.implementation.toLowerCase() === implementation.toLowerCase())
}

export function normalizeImplementation(raw: RawImplementation): ClientImplementation {
  const known = knownOf(raw.implementation)
  const fields = (raw.fields ?? []).map(normalizeField)
  // A schema item shaped like a client resource: defaults in a settings dictionary.
  if (raw.settings) for (const f of fields) if (f.value == null && raw.settings[f.name] !== undefined) f.value = raw.settings[f.name]
  return {
    implementation: raw.implementation,
    name: raw.implementationName ?? known?.name ?? raw.name ?? raw.implementation,
    protocol: raw.protocol ?? 'torrent',
    description: raw.description ?? known?.description ?? null,
    infoUrl: raw.infoUrl ?? raw.infoLink ?? known?.infoUrl ?? null,
    fields,
  }
}

export function normalizeClient(raw: RawClient): DownloadClient {
  const settings: Record<string, ClientValue> = { ...(raw.settings ?? {}) }
  for (const f of raw.fields ?? []) if (settings[f.name] === undefined) settings[f.name] = f.value ?? null
  const implementation = raw.implementation ?? ''
  return {
    id: raw.id,
    name: raw.name ?? '',
    implementation,
    implementationName: raw.implementationName ?? knownOf(implementation)?.name ?? implementation,
    protocol: raw.protocol ?? 'torrent',
    enable: raw.enable ?? true,
    priority: raw.priority ?? 1,
    removeCompleted: raw.removeCompleted ?? raw.removeCompletedDownloads ?? true,
    settings,
    status: normalizeStatus(raw.status),
  }
}

function normalizeStatus(raw: RawStatus | null | undefined): ClientStatus | null {
  if (!raw) return null
  return {
    healthy: raw.healthy ?? raw.ok ?? true,
    message: raw.message ?? raw.error ?? null,
    version: raw.version ?? null,
    checkedAt: raw.checkedAt ?? raw.lastContact ?? null,
  }
}

/** A new client of this implementation, with the schema's defaults. */
export function newClient(type: ClientImplementation, existing: DownloadClient[]): DownloadClient {
  const taken = new Set(existing.map((c) => c.name.toLowerCase()))
  const base = type.implementation === builtInImplementation ? 'StreamZH' : type.name
  let name = base
  for (let n = 2; taken.has(name.toLowerCase()); n++) name = `${base} ${n}`
  const settings: Record<string, ClientValue> = {}
  for (const f of type.fields) settings[f.name] = f.value ?? (f.type === 'checkbox' ? false : f.type === 'number' ? null : '')
  const priorities = existing.filter((c) => c.protocol === type.protocol).map((c) => c.priority)
  return {
    id: 0,
    name,
    implementation: type.implementation,
    implementationName: type.name,
    protocol: type.protocol,
    enable: true,
    priority: priorities.length === 0 ? 1 : Math.min(50, Math.max(...priorities) + 1),
    removeCompleted: true,
    settings,
    status: null,
  }
}

/** What the server gets: the resource with the settings dictionary (and Radarr-style fields, for either reading). */
function toResource(client: DownloadClient) {
  return {
    id: client.id,
    name: client.name.trim(),
    implementation: client.implementation,
    protocol: client.protocol,
    enable: client.enable,
    priority: client.priority,
    removeCompleted: client.removeCompleted,
    settings: client.settings,
  }
}

/** The test answers either { message } or the client's info { name, version, categories }. */
function testMessage(raw: unknown, client: DownloadClient): ClientTestResult {
  const r = (raw ?? {}) as { ok?: boolean; success?: boolean; message?: string; name?: string; version?: string; categories?: string[] }
  if (r.message) return { ok: r.ok ?? r.success ?? true, message: r.message }
  const app = r.name || client.implementationName
  const version = r.version ? ` ${r.version}` : ''
  const categories = r.categories?.length ? `, ${r.categories.length} categor${r.categories.length === 1 ? 'y' : 'ies'}` : ''
  return { ok: true, message: `Connected to ${app}${version}${categories}.` }
}

export const downloadClientKeys = {
  all: ['download-clients'] as const,
  list: ['download-clients', 'list'] as const,
  schema: ['download-clients', 'schema'] as const,
  mappings: ['remote-path-mappings'] as const,
  engine: ['settings', 'torrentengine'] as const,
  engineStatus: ['torrentengine', 'status'] as const,
}

export const downloadClientsApi = {
  list: () => request<RawClient[]>('GET', '/downloadclient').then((list) => list.map(normalizeClient)),
  schema: () => request<RawImplementation[]>('GET', '/downloadclient/schema').then((list) => list.map(normalizeImplementation)),
  create: (client: DownloadClient) => request<RawClient>('POST', '/downloadclient', toResource(client)).then(normalizeClient),
  update: (client: DownloadClient) => request<RawClient>('PUT', `/downloadclient/${client.id}`, toResource(client)).then(normalizeClient),
  remove: (id: number) => request<void>('DELETE', `/downloadclient/${id}`),
  test: (client: DownloadClient) => request<unknown>('POST', '/downloadclient/test', toResource(client)).then((r) => testMessage(r, client)),

  mappings: () => request<RemotePathMapping[]>('GET', '/remotepathmapping'),
  saveMapping: (m: RemotePathMapping) =>
    m.id > 0 ? request<RemotePathMapping>('PUT', `/remotepathmapping/${m.id}`, m) : request<RemotePathMapping>('POST', '/remotepathmapping', m),
  removeMapping: (id: number) => request<void>('DELETE', `/remotepathmapping/${id}`),

  engine: () => request<TorrentEngineSettings>('GET', '/settings/torrentengine'),
  saveEngine: (settings: TorrentEngineSettings) => request<TorrentEngineSettings>('PUT', '/settings/torrentengine', settings),
  engineStatus: () => request<EngineStatus>('GET', '/torrentengine/status'),
}

/** Host of a client from its settings ("localhost:8080"); the built-in engine has none. */
export function clientAddress(client: DownloadClient): string | null {
  const host = typeof client.settings.host === 'string' ? client.settings.host : null
  if (!host) return null
  const port = client.settings.port
  return port != null && port !== '' ? `${host}:${port}` : host
}
