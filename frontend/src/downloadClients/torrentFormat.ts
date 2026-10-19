// Formatting and flag logic for torrents, shared by the release tables, Activity and the settings.

/** Health of a swarm by seeders: none is dead, a few is slow, more is fine (Radarr colours the peers cell alike). */
export function seedTone(seeders: number | null | undefined): 'dead' | 'low' | 'ok' | 'unknown' {
  if (seeders == null) return 'unknown'
  if (seeders <= 0) return 'dead'
  if (seeders < 5) return 'low'
  return 'ok'
}

/** Radarr/Sonarr IndexerFlags bits. */
export const indexerFlagBits = {
  freeleech: 1,
  halfleech: 2,
  doubleUpload: 4,
  golden: 8,
  approved: 16,
  internal: 32,
  internalAhd: 64,
  scene: 128,
  freeleech75: 256,
  freeleech25: 512,
  nuked: 2048,
} as const

/** Flags as a bit mask; servers may also send names like "G_Freeleech" or "freeleech". */
export function flagMask(flags: number | string[] | null | undefined): number {
  if (typeof flags === 'number') return flags
  if (!Array.isArray(flags)) return 0
  const byName: Record<string, number> = {
    g_freeleech: 1,
    freeleech: 1,
    g_halfleech: 2,
    halfleech: 2,
    g_doubleupload: 4,
    doubleupload: 4,
    ptp_golden: 8,
    golden: 8,
    ptp_approved: 16,
    approved: 16,
    g_internal: 32,
    internal: 32,
    ahd_internal: 64,
    g_scene: 128,
    scene: 128,
    g_freeleech75: 256,
    freeleech75: 256,
    g_freeleech25: 512,
    freeleech25: 512,
    nuked: 2048,
  }
  return flags.reduce((mask, f) => mask | (byName[f.toLowerCase().replace(/[^a-z0-9_]/g, '')] ?? 0), 0)
}

export interface TrackerFlag {
  key: string
  label: string
  tone: 'free' | 'up' | 'info' | 'bad'
  title: string
}

/** The chips to show for a torrent release: freeleech (from the volume factor or the flags), double upload, scene … */
export function trackerFlags(release: {
  downloadVolumeFactor?: number | null
  uploadVolumeFactor?: number | null
  indexerFlags?: number | string[] | null
  flags?: string[] | null
}): TrackerFlag[] {
  const mask = flagMask(release.indexerFlags) | flagMask(release.flags ?? [])
  const has = (bit: number) => (mask & bit) === bit
  const result: TrackerFlag[] = []
  const factor = release.downloadVolumeFactor
  // The factor is what counts against the account: 0 = free, 0.25 = 75 % off.
  const off = factor != null && factor < 1 ? Math.round((1 - factor) * 100) : has(indexerFlagBits.freeleech) ? 100 : has(indexerFlagBits.freeleech75) ? 75 : has(indexerFlagBits.halfleech) ? 50 : has(indexerFlagBits.freeleech25) ? 25 : 0
  if (off >= 100) result.push({ key: 'free', label: 'Freeleech', tone: 'free', title: 'Downloading does not count against your ratio' })
  else if (off > 0) result.push({ key: 'free', label: `${off}% off`, tone: 'free', title: `Only ${100 - off}% of the download counts against your ratio` })
  if ((release.uploadVolumeFactor ?? 1) >= 2 || has(indexerFlagBits.doubleUpload))
    result.push({ key: 'up', label: `${Math.max(2, Math.round(release.uploadVolumeFactor ?? 2))}× upload`, tone: 'up', title: 'Uploads count more towards your ratio' })
  if (has(indexerFlagBits.golden)) result.push({ key: 'golden', label: 'Golden', tone: 'info', title: 'Golden popcorn' })
  if (has(indexerFlagBits.internal) || has(indexerFlagBits.internalAhd)) result.push({ key: 'internal', label: 'Internal', tone: 'info', title: 'Internal release of the tracker' })
  if (has(indexerFlagBits.scene)) result.push({ key: 'scene', label: 'Scene', tone: 'info', title: 'Scene release' })
  if (has(indexerFlagBits.nuked)) result.push({ key: 'nuked', label: 'Nuked', tone: 'bad', title: 'The release was nuked' })
  return result
}

/** "1.24" (two decimals below 10, one above). */
export function formatRatio(ratio: number | null | undefined): string {
  if (ratio == null || Number.isNaN(ratio)) return '–'
  return ratio < 10 ? ratio.toFixed(2) : ratio.toFixed(1)
}

/** Name of the torrent client that has a download ("downloadClient" from the server, "clientName" in older drafts). */
export function clientNameOf(item: { downloadClient?: string | null; clientName?: string | null }): string | null {
  return item.downloadClient ?? item.clientName ?? null
}

/** True for downloads that run in a torrent client (older servers send no protocol: Usenet). */
export function isTorrent(item: { protocol?: 'usenet' | 'torrent' | null }): boolean {
  return item.protocol === 'torrent'
}
