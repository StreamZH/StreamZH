const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB']

export function formatBytes(bytes: number | null | undefined): string {
  if (bytes == null || bytes <= 0) return '0 B'
  const exponent = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1)
  const value = bytes / 1024 ** exponent
  return `${value >= 100 || exponent === 0 ? Math.round(value) : value.toFixed(1)} ${units[exponent]}`
}

export function formatDate(value: string | null | undefined): string {
  if (!value) return '–'
  const date = new Date(value.length === 10 ? `${value}T00:00:00` : value)
  return date.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' })
}

export function formatRuntime(minutes: number): string {
  if (!minutes) return ''
  const h = Math.floor(minutes / 60)
  const m = minutes % 60
  return h > 0 ? `${h}h ${m}m` : `${m}m`
}

export function isAired(airDate: string | null): boolean {
  if (!airDate) return false
  return airDate <= new Date().toISOString().slice(0, 10)
}

/**
 * TMDB image through StreamZH: from its image cache when that is on (Settings → Metadata), otherwise a redirect to
 * the TMDB image CDN. Sizes: w92, w185, w342, w500, w780, w1280, original.
 */
export function tmdbImage(path: string | null | undefined, size = 'w342'): string | null {
  return path ? `/api/v1/mediacover/${size}${path}` : null
}
