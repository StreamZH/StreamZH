import { useSyncExternalStore } from 'react'

const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB']

export function formatBytes(bytes: number | null | undefined): string {
  if (bytes == null || bytes <= 0) return '0 B'
  const exponent = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1)
  const value = bytes / 1024 ** exponent
  return `${value >= 100 || exponent === 0 ? Math.round(value) : value.toFixed(1)} ${units[exponent]}`
}

// ---- Dates and times: one place for the whole app, following Settings → UI ----
// "locale" means the browser's own format, which is what StreamZH showed before the UI settings existed.
// Patterns use Radarr's tokens: YYYY, MMMM, MMM, MM, DD, D, dddd, HH, h, mm, a.

export interface UiFormats {
  shortDateFormat: string
  longDateFormat: string
  timeFormat: string
  showRelativeDates: boolean
}

const locale = 'locale'
const defaults: UiFormats = { shortDateFormat: locale, longDateFormat: locale, timeFormat: locale, showRelativeDates: true }
const storageKey = 'streamzh-ui-formats'
const listeners = new Set<() => void>()

function readStored(): UiFormats {
  try {
    const stored = JSON.parse(localStorage.getItem(storageKey) ?? 'null') as Partial<UiFormats> | null
    return stored ? { ...defaults, ...stored } : defaults
  } catch {
    return defaults
  }
}

let current = readStored()

/** Applies the instance's UI settings (loaded once after sign-in, and after saving Settings → UI). */
export function setUiFormats(formats: UiFormats) {
  const next: UiFormats = {
    shortDateFormat: formats.shortDateFormat,
    longDateFormat: formats.longDateFormat,
    timeFormat: formats.timeFormat,
    showRelativeDates: formats.showRelativeDates,
  }
  if (JSON.stringify(next) === JSON.stringify(current)) return
  current = next
  try {
    localStorage.setItem(storageKey, JSON.stringify(next))
  } catch {
    // no storage: the formats still apply until the page is reloaded
  }
  listeners.forEach((listener) => listener())
}

/** The current formats; components using it re-render when Settings → UI is saved. */
export function useUiFormats(): UiFormats {
  return useSyncExternalStore(
    (listener) => {
      listeners.add(listener)
      return () => listeners.delete(listener)
    },
    () => current,
    () => defaults,
  )
}

type DateInput = string | number | Date | null | undefined

/** Date-only values ("2026-10-17") are local days, not UTC midnight. */
function toDate(value: DateInput): Date | null {
  if (value == null || value === '') return null
  const date = value instanceof Date ? value : new Date(typeof value === 'string' && value.length === 10 ? `${value}T00:00:00` : value)
  return Number.isNaN(date.getTime()) ? null : date
}

const english = 'en-US'

function pad(value: number): string {
  return String(value).padStart(2, '0')
}

/** Formats a date with Radarr-style tokens. */
export function formatPattern(date: Date, pattern: string): string {
  return pattern.replace(/YYYY|MMMM|MMM|MM|dddd|DD|D|HH|h|mm|a/g, (token) => {
    switch (token) {
      case 'YYYY':
        return String(date.getFullYear())
      case 'MMMM':
        return date.toLocaleDateString(english, { month: 'long' })
      case 'MMM':
        return date.toLocaleDateString(english, { month: 'short' })
      case 'MM':
        return pad(date.getMonth() + 1)
      case 'dddd':
        return date.toLocaleDateString(english, { weekday: 'long' })
      case 'DD':
        return pad(date.getDate())
      case 'D':
        return String(date.getDate())
      case 'HH':
        return pad(date.getHours())
      case 'h':
        return String(date.getHours() % 12 || 12)
      case 'mm':
        return pad(date.getMinutes())
      case 'a':
        return date.getHours() < 12 ? 'AM' : 'PM'
      default:
        return token
    }
  })
}

/** The short date pattern without the year, for "Oct 17, 14:05" and chart labels. */
function withoutYear(pattern: string): string {
  return pattern.replace(/^YYYY[^A-Za-z]*/, '').replace(/[^A-Za-z]*YYYY$/, '')
}

/** "Oct 17, 2026" (Settings → UI → Short date). */
export function formatDate(value: DateInput, formats: UiFormats = current): string {
  const date = toDate(value)
  if (!date) return '–'
  return formats.shortDateFormat === locale
    ? date.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' })
    : formatPattern(date, formats.shortDateFormat)
}

/** "Saturday, October 17 2026" (Settings → UI → Long date). */
export function formatLongDate(value: DateInput, formats: UiFormats = current): string {
  const date = toDate(value)
  if (!date) return '–'
  return formats.longDateFormat === locale
    ? date.toLocaleDateString(undefined, { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' })
    : formatPattern(date, formats.longDateFormat)
}

/** "14:05" or "2:05 PM" (Settings → UI → Time format). */
export function formatTime(value: DateInput, formats: UiFormats = current): string {
  const date = toDate(value)
  if (!date) return '–'
  return formats.timeFormat === locale ? date.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' }) : formatPattern(date, formats.timeFormat)
}

/** Full date and time, e.g. for tooltips. */
export function formatDateTime(value: DateInput, formats: UiFormats = current): string {
  const date = toDate(value)
  if (!date) return '–'
  if (formats.shortDateFormat === locale && formats.timeFormat === locale) return date.toLocaleString()
  return `${formatDate(date, formats)} ${formatTime(date, formats)}`
}

/** "Oct 17" without the year. */
export function formatDayMonth(value: DateInput, formats: UiFormats = current): string {
  const date = toDate(value)
  if (!date) return '–'
  return formats.shortDateFormat === locale
    ? date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })
    : formatPattern(date, withoutYear(formats.shortDateFormat))
}

/** "Oct 17, 14:05". */
export function formatDayMonthTime(value: DateInput, formats: UiFormats = current): string {
  return `${formatDayMonth(value, formats)}, ${formatTime(value, formats)}`
}

/** "14:05" today, "Oct 17, 14:05" on another day (always 24 h unless Settings → UI says otherwise). */
export function formatUntil(value: DateInput, formats: UiFormats = current): string {
  const date = toDate(value)
  if (!date) return '–'
  const time =
    formats.timeFormat === locale ? date.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }) : formatTime(date, formats)
  if (date.toDateString() === new Date().toDateString()) return time
  return `${formatDayMonth(date, formats)}, ${time}`
}

/** "just now", "3 min ago", "2 h ago", older ones as "Oct 17, 14:05"; always absolute without relative dates. */
export function relativeTime(value: DateInput, formats: UiFormats = current): string {
  const date = toDate(value)
  if (!date) return ''
  if (!formats.showRelativeDates) return formatDayMonthTime(date, formats)
  const seconds = (Date.now() - date.getTime()) / 1000
  if (seconds < 60) return 'just now'
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`
  if (seconds < 86_400) return `${Math.floor(seconds / 3600)} h ago`
  return formatDayMonthTime(date, formats)
}

/** "3 minutes ago", "in 2 days" (past and future); absolute without relative dates. */
export function formatAgo(value: DateInput, formats: UiFormats = current): string {
  const date = toDate(value)
  if (!date) return ''
  if (!formats.showRelativeDates) return formatDayMonthTime(date, formats)
  const seconds = Math.round((date.getTime() - Date.now()) / 1000)
  const rtf = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' })
  const abs = Math.abs(seconds)
  if (abs < 60) return rtf.format(seconds, 'second')
  if (abs < 3600) return rtf.format(Math.round(seconds / 60), 'minute')
  if (abs < 86400) return rtf.format(Math.round(seconds / 3600), 'hour')
  return rtf.format(Math.round(seconds / 86400), 'day')
}

/** "3 d 4 h", "12 min" for durations like uptime. */
export function formatDuration(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds))
  const days = Math.floor(s / 86_400)
  const hours = Math.floor((s % 86_400) / 3600)
  const minutes = Math.floor((s % 3600) / 60)
  if (days > 0) return hours ? `${days} d ${hours} h` : `${days} d`
  if (hours > 0) return minutes ? `${hours} h ${minutes} min` : `${hours} h`
  if (minutes > 0) return `${minutes} min`
  return `${s} s`
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

const imageBase = 'https://image.tmdb.org/t/p/'

/** TMDB image URL. Sizes: w92, w185, w342, w500, w780, w1280, original. */
export function tmdbImage(path: string | null | undefined, size = 'w342'): string | null {
  return path ? `${imageBase}${size}${path}` : null
}
