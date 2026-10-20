/** Precise numbers for running downloads (exact percent and time left). */

const pct = new Intl.NumberFormat(undefined, { minimumFractionDigits: 1, maximumFractionDigits: 1 })

/** 0.4734 → "47.3 %"; never shows 100.0 % before the end. */
export function precisePercent(value: number | null | undefined): string {
  const v = Math.min(1, Math.max(0, value ?? 0))
  const shown = v < 1 ? Math.min(Math.floor(v * 1000) / 10, 99.9) : 100
  return `${pct.format(shown)} %`
}

const pad = (n: number) => String(n).padStart(2, '0')

/** Seconds → "45 s", "12 min 05 s", "1 h 02 min"; '' when unknown. */
export function formatLeft(seconds: number | null | undefined): string {
  if (seconds == null || !Number.isFinite(seconds) || seconds < 0) return ''
  const s = Math.ceil(seconds)
  if (s < 60) return `${s} s`
  if (s < 3600) return `${Math.floor(s / 60)} min ${pad(s % 60)} s`
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  return h < 100 ? `${h} h ${pad(m)} min` : `${h} h`
}
