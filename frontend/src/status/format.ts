import { formatBytes } from '../lib/format'

/** "34 %": rounded down, so 100 % only shows when it is really done. */
export function percent(value: number | null | undefined): string {
  return `${Math.floor(Math.min(1, Math.max(0, value ?? 0)) * 100)} %`
}

export function formatSpeed(bytesPerSecond: number | null | undefined): string {
  return `${formatBytes(bytesPerSecond ?? 0)}/s`
}

/** "3 min left", "1 h 12 min left", "under a minute left". */
export function formatEta(seconds: number | null | undefined): string {
  if (seconds == null || !Number.isFinite(seconds)) return ''
  if (seconds < 60) return 'under a minute left'
  if (seconds < 3600) return `${Math.ceil(seconds / 60)} min left`
  const hours = Math.floor(seconds / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  return minutes ? `${hours} h ${minutes} min left` : `${hours} h left`
}

/** "2:14" or "1:02:14" for elapsed time. */
export function formatElapsed(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000))
  const h = Math.floor(total / 3600)
  const m = Math.floor((total % 3600) / 60)
  const s = String(total % 60).padStart(2, '0')
  return h ? `${h}:${String(m).padStart(2, '0')}:${s}` : `${m}:${s}`
}

/** "3 min ago"; follows Settings → UI (see lib/format). */
export { relativeTime } from '../lib/format'

export function joinParts(...parts: (string | null | undefined | false)[]): string {
  return parts.filter(Boolean).join(' · ')
}
