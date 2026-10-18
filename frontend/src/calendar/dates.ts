// Calendar days are local days written as yyyy-MM-dd, like the dates TMDB gives (no time, no time zone).

export type CalendarView = 'month' | 'week' | 'agenda'

/** Days the agenda shows at once. */
export const agendaDays = 30

function pad(value: number): string {
  return String(value).padStart(2, '0')
}

export function dayKey(date: Date): string {
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`
}

/** "2026-10-18" → local midnight of that day; null for anything else. */
export function parseDay(value: string | null | undefined): Date | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value ?? '')
  if (!match) return null
  const date = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]))
  return Number.isNaN(date.getTime()) || dayKey(date) !== value ? null : date
}

export function addDays(date: Date, days: number): Date {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate() + days)
}

export function addMonths(date: Date, months: number): Date {
  return new Date(date.getFullYear(), date.getMonth() + months, 1)
}

/** 0 = Sunday, 1 = Monday. */
export function startOfWeek(date: Date, firstDay: number): Date {
  return addDays(date, -((date.getDay() - firstDay + 7) % 7))
}

/** First and last day (inclusive) a view shows around the anchor day. Months show whole weeks. */
export function viewRange(view: CalendarView, anchor: Date, firstDay: number): { start: Date; end: Date } {
  if (view === 'week') {
    const start = startOfWeek(anchor, firstDay)
    return { start, end: addDays(start, 6) }
  }
  if (view === 'agenda') {
    return { start: anchor, end: addDays(anchor, agendaDays - 1) }
  }
  const first = new Date(anchor.getFullYear(), anchor.getMonth(), 1)
  const last = new Date(anchor.getFullYear(), anchor.getMonth() + 1, 0)
  return { start: startOfWeek(first, firstDay), end: addDays(startOfWeek(last, firstDay), 6) }
}

/** The anchor after "previous" (-1) or "next" (+1). */
export function step(view: CalendarView, anchor: Date, direction: -1 | 1): Date {
  if (view === 'month') return addMonths(anchor, direction)
  return addDays(anchor, direction * (view === 'week' ? 7 : agendaDays))
}

/** All days from start to end (inclusive). */
export function daysBetween(start: Date, end: Date): Date[] {
  const days: Date[] = []
  for (let day = start; day <= end; day = addDays(day, 1)) days.push(day)
  return days
}

export function weekdayNames(firstDay: number, style: 'short' | 'long' = 'short'): string[] {
  // 2026-10-18 is a Sunday.
  return Array.from({ length: 7 }, (_, i) => new Date(2026, 9, 18 + firstDay + i).toLocaleDateString('en-US', { weekday: style }))
}
