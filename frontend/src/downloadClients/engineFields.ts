import type { ClientValue } from '../api/downloadClients'

// How the built-in torrent engine's settings show (GET /settings/torrentengine). The server decides which keys exist;
// known keys get a label, help and unit here (several spellings each), anything else shows as an expert setting with a
// label made from its name, so a new engine option never needs a frontend release to be editable.

export interface EngineFieldMeta {
  label: string
  help?: string
  unit?: string
  min?: number
  max?: number
  step?: number
  advanced?: boolean
  /** Fixed choices (value → label). */
  options?: { value: string; label: string }[]
  /** Sort order on the page. */
  order: number
  kind?: 'path'
}

const speed = (label: string, help: string, order: number, advanced = false): EngineFieldMeta => ({ label, help, unit: 'KB/s', min: 0, step: 100, order, advanced })

const known: [string[], EngineFieldMeta][] = [
  [['downloadFolder', 'savePath', 'downloadPath', 'folder'], { label: 'Download folder', help: 'Where torrents are saved while they download and seed. Empty: the complete folder from Settings → Downloads.', order: 1, kind: 'path' }],
  [['bindAddress', 'listenAddress'], { label: 'Bind address', help: 'Empty: all network interfaces. Otherwise the only IP address the engine uses.', order: 15, advanced: true }],
  [['listenPort', 'port', 'incomingPort'], { label: 'Listening port', help: 'Other peers connect to this TCP and UDP port. Forward it on your router for faster downloads.', min: 1, max: 65535, order: 2 }],
  [['maxDownloadSpeedKBps', 'downloadLimitKBps', 'maxDownloadKBps', 'downloadSpeedLimitKBps'], speed('Download limit', '0 = unlimited.', 3)],
  [['maxUploadSpeedKBps', 'uploadLimitKBps', 'maxUploadKBps', 'uploadSpeedLimitKBps'], speed('Upload limit', '0 = unlimited. Private trackers expect you to give back.', 4)],
  [['seedRatio', 'defaultSeedRatio', 'seedRatioLimit'], { label: 'Seed ratio', help: 'Seed until this ratio when the indexer sets none. Empty or 0: no limit.', min: 0, step: 0.1, order: 5 }],
  [['seedTimeMinutes', 'defaultSeedTimeMinutes', 'seedTimeLimitMinutes'], { label: 'Seed time', help: 'Seed at least this long when the indexer sets none. Empty or 0: no limit.', unit: 'minutes', min: 0, step: 60, order: 6 }],
  [['maxActiveDownloads', 'maxActiveTorrents', 'activeDownloads'], { label: 'Active downloads', help: 'Torrents downloading at the same time; the rest wait in the queue.', min: 1, order: 10, advanced: true }],
  [['maxActiveSeeds', 'maxActiveUploads', 'activeSeeds'], { label: 'Active seeds', help: 'Torrents seeding at the same time. 0 = unlimited.', min: 0, order: 11, advanced: true }],
  [['maxConnections', 'globalMaxConnections', 'connectionLimit'], { label: 'Maximum connections', help: 'Peer connections over all torrents.', min: 1, order: 12, advanced: true }],
  [['maxConnectionsPerTorrent', 'connectionsPerTorrent'], { label: 'Connections per torrent', min: 1, order: 13, advanced: true }],
  [['maxUploadSlots', 'uploadSlots'], { label: 'Upload slots', help: 'Peers to upload to at the same time per torrent.', min: 1, order: 14, advanced: true }],
  [['dht', 'enableDht', 'dhtEnabled', 'allowDht'], { label: 'DHT', help: 'Find peers without the tracker. Private torrents never use it.', order: 20, advanced: true }],
  [['pex', 'enablePex', 'pexEnabled', 'allowPex', 'peerExchange'], { label: 'Peer exchange (PEX)', help: 'Learn peers from other peers. Private torrents never use it.', order: 21, advanced: true }],
  [['lsd', 'localPeerDiscovery', 'enableLsd', 'allowLocalPeerDiscovery'], { label: 'Local peer discovery', help: 'Find peers in your own network.', order: 22, advanced: true }],
  [
    ['encryption', 'encryptionMode', 'encryptionPolicy'],
    {
      label: 'Encryption',
      help: 'Preferred encrypts when the peer can. Required only talks to peers that encrypt. Allowed starts unencrypted.',
      order: 23,
      advanced: true,
      options: [
        { value: 'prefer', label: 'Preferred' },
        { value: 'require', label: 'Required' },
        { value: 'allow', label: 'Allowed (plain first)' },
      ],
    },
  ],
  [['upnp', 'enableUpnp', 'portForwarding', 'natPmp', 'allowPortForwarding'], { label: 'UPnP / NAT-PMP', help: 'Ask the router to open the listening port. Off unless you turn it on.', order: 24, advanced: true }],
  [['altSpeedEnabled', 'alternativeSpeedEnabled', 'useAlternativeSpeed'], { label: 'Alternative speed limits', help: 'Use the limits below instead of the normal ones.', order: 30, advanced: true }],
  [['altDownloadSpeedKBps', 'alternativeDownloadLimitKBps', 'altDownloadLimitKBps'], speed('Alternative download limit', '0 = unlimited.', 31, true)],
  [['altUploadSpeedKBps', 'alternativeUploadLimitKBps', 'altUploadLimitKBps'], speed('Alternative upload limit', '0 = unlimited.', 32, true)],
]

const byKey = new Map<string, EngineFieldMeta>()
for (const [keys, meta] of known) for (const key of keys) byKey.set(key.toLowerCase(), meta)

/** "maxHalfOpenConnections" → "Max half open connections". */
function labelOf(key: string): string {
  const words = key
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
    .toLowerCase()
    .replace(/\bkbps\b/, '(KB/s)')
  return words.charAt(0).toUpperCase() + words.slice(1)
}

export interface EngineField extends EngineFieldMeta {
  key: string
  type: 'bool' | 'number' | 'text' | 'select' | 'path'
}

/** The editable settings in page order; values that are not plain (objects, lists) are kept but not shown. */
export function engineFields(settings: Record<string, ClientValue | object>): EngineField[] {
  const fields: EngineField[] = []
  for (const [key, value] of Object.entries(settings)) {
    if (value !== null && typeof value === 'object') continue
    const meta = byKey.get(key.toLowerCase())
    const type: EngineField['type'] = meta?.options
      ? 'select'
      : meta?.kind === 'path'
        ? 'path'
        : typeof value === 'boolean'
          ? 'bool'
          : typeof value === 'number' || (value === null && meta?.min !== undefined)
            ? 'number'
            : 'text'
    fields.push({ key, type, ...(meta ?? { label: labelOf(key), advanced: true, order: 100 }) })
  }
  return fields.sort((a, b) => a.order - b.order || a.label.localeCompare(b.label))
}
