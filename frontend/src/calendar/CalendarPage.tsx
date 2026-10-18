import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useSearchParams } from 'react-router'
import { generalApi, generalKeys } from '../api/general'
import { calendarApi, calendarKeys, type CalendarEntry, type CalendarFilter } from '../api/wanted'
import ui from '../components/ui.module.css'
import { formatDate, formatDayMonth, formatLongDate, useUiFormats } from '../lib/format'
import page from '../pages/Page.module.css'
import { useIsAdmin } from '../status/useOngoing'
import Glyph from '../wanted/Glyph'
import { addDays, dayKey, daysBetween, parseDay, step, viewRange, weekdayNames, type CalendarView } from './dates'
import IcalDialog from './IcalDialog'
import styles from './Calendar.module.css'

const views: { key: CalendarView; label: string }[] = [
  { key: 'month', label: 'Month' },
  { key: 'week', label: 'Week' },
  { key: 'agenda', label: 'Agenda' },
]

const releaseLabels = { inCinemas: 'In cinemas', digital: 'Digital release', physical: 'Physical release', airDate: 'Airs' } as const
const shortReleaseLabels = { inCinemas: 'Cinema', digital: 'Digital', physical: 'Physical', airDate: '' } as const
const statusLabels = { downloaded: 'Downloaded', downloading: 'Downloading', missing: 'Missing', unaired: 'Not released yet' } as const

/** Per-browser view and filters (a convenience; the page works without storage). */
const storageKey = 'streamzh-calendar'

interface Stored {
  view?: CalendarView
  movies?: boolean
  series?: boolean
  unmonitored?: boolean
}

function readStored(): Stored {
  try {
    return (JSON.parse(localStorage.getItem(storageKey) ?? '{}') as Stored) ?? {}
  } catch {
    return {}
  }
}

function writeStored(value: Stored) {
  try {
    localStorage.setItem(storageKey, JSON.stringify(value))
  } catch {
    // no storage: the choice lasts until the page is reloaded
  }
}

function isPhone(): boolean {
  return typeof window !== 'undefined' && window.matchMedia?.('(max-width: 760px)').matches === true
}

function episodeCode(e: CalendarEntry): string {
  return `${e.seasonNumber}x${String(e.episodeNumber).padStart(2, '0')}`
}

function entryLink(e: CalendarEntry): string {
  return e.type === 'movie' ? `/library/movies/${e.movieId}` : `/library/series/${e.seriesId}`
}

function entryLabel(e: CalendarEntry): string {
  return e.type === 'movie'
    ? `${e.title}, ${releaseLabels[e.releaseType]}, ${statusLabels[e.status]}`
    : `${e.title} ${episodeCode(e)}${e.episodeTitle ? ` ${e.episodeTitle}` : ''}, ${statusLabels[e.status]}`
}

/** Calendar like Radarr and Sonarr: movie releases and episode air dates by month, week or as a list. */
export default function CalendarPage() {
  const [url, setUrl] = useSearchParams()
  const admin = useIsAdmin()
  const formats = useUiFormats()
  const [stored, setStored] = useState<Stored>(readStored)
  const [selectedDay, setSelectedDay] = useState<string | null>(null)
  const [ical, setIcal] = useState(false)

  const uiSettings = useQuery({ queryKey: generalKeys.ui, queryFn: generalApi.ui, staleTime: 5 * 60_000 })
  const firstDay = uiSettings.data?.firstDayOfWeek === 'sunday' ? 0 : 1

  const requestedView = views.find((v) => v.key === url.get('view'))?.key
  const view: CalendarView = requestedView ?? stored.view ?? (isPhone() ? 'agenda' : 'month')
  const [today] = useState(() => new Date())
  const todayKey = dayKey(today)
  const anchor = parseDay(url.get('date')) ?? new Date(today.getFullYear(), today.getMonth(), today.getDate())
  const { start, end } = viewRange(view, anchor, firstDay)

  const showMovies = stored.movies ?? true
  const showSeries = stored.series ?? true
  const filter: CalendarFilter = {
    start: dayKey(start),
    end: dayKey(end),
    unmonitored: stored.unmonitored ?? false,
    type: showMovies && showSeries ? 'all' : showMovies ? 'movies' : 'series',
  }
  const nothingShown = !showMovies && !showSeries

  const entries = useQuery({
    queryKey: calendarKeys.range(filter),
    queryFn: () => calendarApi.entries(filter),
    placeholderData: (previous) => previous,
    enabled: !nothingShown,
  })

  const byDay = new Map<string, CalendarEntry[]>()
  for (const entry of nothingShown ? [] : (entries.data ?? [])) {
    const list = byDay.get(entry.date) ?? []
    list.push(entry)
    byDay.set(entry.date, list)
  }

  const go = (changes: { view?: CalendarView; date?: Date | null }) => {
    const next = new URLSearchParams(url)
    if (changes.view) next.set('view', changes.view)
    if (changes.date !== undefined) {
      if (changes.date && dayKey(changes.date) !== todayKey) next.set('date', dayKey(changes.date))
      else next.delete('date')
    }
    setUrl(next)
    setSelectedDay(null)
  }

  const remember = (change: Stored) => {
    const next = { ...stored, ...change }
    setStored(next)
    writeStored(next)
  }

  const title =
    view === 'month'
      ? anchor.toLocaleDateString('en-US', { month: 'long', year: 'numeric' })
      : `${formatDayMonth(start, formats)} – ${formatDate(end, formats)}`

  const inRange = todayKey >= dayKey(start) && todayKey <= dayKey(end)

  return (
    <div className={`${page.page} ${styles.calendar}`}>
      <header className={styles.header}>
        <div>
          <h1 className={page.title}>Calendar</h1>
          <p className={page.lead}>Movie releases and episode air dates of your library, and what is already on disk.</p>
        </div>
        {admin && (
          <button type="button" className={ui.button} onClick={() => setIcal(true)}>
            <Glyph name="link" size={14} /> iCal link
          </button>
        )}
      </header>

      <div className={styles.toolbar}>
        <div className={styles.nav}>
          <button type="button" className={ui.button} onClick={() => go({ date: null })} disabled={inRange && !url.get('date')}>
            Today
          </button>
          <button type="button" className={styles.arrow} onClick={() => go({ date: step(view, anchor, -1) })} aria-label="Previous">
            <Glyph name="left" size={16} />
          </button>
          <button type="button" className={styles.arrow} onClick={() => go({ date: step(view, anchor, 1) })} aria-label="Next">
            <Glyph name="right" size={16} />
          </button>
          <h2 className={styles.period} aria-live="polite">
            {title}
          </h2>
        </div>
        <div className={styles.segmented} role="group" aria-label="View">
          {views.map((v) => (
            <button
              key={v.key}
              type="button"
              aria-pressed={view === v.key}
              onClick={() => {
                remember({ view: v.key })
                go({ view: v.key })
              }}
            >
              {v.label}
            </button>
          ))}
        </div>
      </div>

      <div className={styles.filters}>
        <button type="button" className={styles.chip} data-tone="movies" aria-pressed={showMovies} onClick={() => remember({ movies: !showMovies })}>
          Movies
        </button>
        <button type="button" className={styles.chip} data-tone="series" aria-pressed={showSeries} onClick={() => remember({ series: !showSeries })}>
          Series
        </button>
        <label className={ui.check}>
          <input type="checkbox" checked={filter.unmonitored} onChange={(e) => remember({ unmonitored: e.target.checked })} />
          Show unmonitored
        </label>
        <ul className={styles.legend} aria-label="Legend">
          <li data-status="downloaded">Downloaded</li>
          <li data-status="downloading">Downloading</li>
          <li data-status="missing">Missing</li>
          <li data-status="unaired">Not released yet</li>
        </ul>
      </div>

      {entries.isError && <p className={ui.error}>{entries.error.message}</p>}
      {nothingShown && <p className={styles.note}>Pick movies, series or both to fill the calendar.</p>}

      <div className={entries.isPlaceholderData ? styles.stale : undefined}>
        {view === 'month' && (
          <MonthView
            anchor={anchor}
            start={start}
            end={end}
            firstDay={firstDay}
            todayKey={todayKey}
            byDay={byDay}
            selectedDay={selectedDay ?? (inRange ? todayKey : null)}
            onSelect={setSelectedDay}
          />
        )}
        {view === 'week' && <WeekView start={start} firstDay={firstDay} todayKey={todayKey} byDay={byDay} />}
        {view === 'agenda' && <AgendaView start={start} end={end} todayKey={todayKey} byDay={byDay} loading={!entries.data && !nothingShown} />}
      </div>

      {ical && <IcalDialog onClose={() => setIcal(false)} />}
    </div>
  )
}

function Pill({ entry, compact }: { entry: CalendarEntry; compact?: boolean }) {
  const label = entry.type === 'movie' ? shortReleaseLabels[entry.releaseType] : episodeCode(entry)
  return (
    <Link
      to={entryLink(entry)}
      className={`${styles.pill} ${compact ? styles.compact : ''}`}
      data-type={entry.type}
      data-status={entry.status}
      data-unmonitored={entry.monitored ? undefined : 'true'}
      title={entryLabel(entry)}
    >
      <span className={styles.pillTitle}>{entry.title}</span>
      {label && <span className={styles.pillMeta}>{label}</span>}
      <span className={styles.statusDot} aria-hidden="true" />
    </Link>
  )
}

function MonthView(props: {
  anchor: Date
  start: Date
  end: Date
  firstDay: number
  todayKey: string
  byDay: Map<string, CalendarEntry[]>
  selectedDay: string | null
  onSelect: (day: string) => void
}) {
  const days = daysBetween(props.start, props.end)
  const month = props.anchor.getMonth()
  const maxShown = 4
  const selected = props.selectedDay ? (props.byDay.get(props.selectedDay) ?? []) : []

  return (
    <>
      <div className={styles.month} role="grid" aria-label="Month">
        <div className={styles.weekdays} role="row">
          {weekdayNames(props.firstDay).map((name) => (
            <span key={name} role="columnheader">
              {name}
            </span>
          ))}
        </div>
        <div className={styles.monthGrid}>
          {days.map((day) => {
            const key = dayKey(day)
            const list = props.byDay.get(key) ?? []
            const outside = day.getMonth() !== month
            return (
              <div
                key={key}
                role="gridcell"
                className={`${styles.day} ${outside ? styles.outside : ''} ${key === props.todayKey ? styles.today : ''} ${key === props.selectedDay ? styles.daySelected : ''}`}
                aria-label={`${formatLongDate(key)}: ${list.length === 0 ? 'nothing' : `${list.length} ${list.length === 1 ? 'entry' : 'entries'}`}`}
              >
                <button type="button" className={styles.dayNumber} onClick={() => props.onSelect(key)} aria-label={`Show ${formatLongDate(key)}`}>
                  {day.getDate()}
                </button>
                <div className={styles.dayEntries}>
                  {list.slice(0, maxShown).map((entry) => (
                    <Pill key={entry.key} entry={entry} compact />
                  ))}
                  {list.length > maxShown && (
                    <button type="button" className={styles.more} onClick={() => props.onSelect(key)}>
                      +{list.length - maxShown} more
                    </button>
                  )}
                </div>
                {/* Phone: the whole day is the button; the entries show below the grid. */}
                <button type="button" className={styles.dayTap} onClick={() => props.onSelect(key)} aria-hidden="true" tabIndex={-1} />
              </div>
            )
          })}
        </div>
      </div>
      {props.selectedDay && (
        <section className={styles.dayList} aria-label={formatLongDate(props.selectedDay)}>
          <h3 className={styles.dayHeading}>
            {formatLongDate(props.selectedDay)}
            {props.selectedDay === props.todayKey && <span className={styles.todayTag}>Today</span>}
          </h3>
          {selected.length === 0 ? <p className={styles.note}>Nothing on this day.</p> : <EntryList entries={selected} />}
        </section>
      )}
    </>
  )
}

function WeekView(props: { start: Date; firstDay: number; todayKey: string; byDay: Map<string, CalendarEntry[]> }) {
  const days = daysBetween(props.start, addDays(props.start, 6))
  const names = weekdayNames(props.firstDay)
  return (
    <div className={styles.week}>
      {days.map((day, i) => {
        const key = dayKey(day)
        const list = props.byDay.get(key) ?? []
        return (
          <section key={key} className={`${styles.weekDay} ${key === props.todayKey ? styles.today : ''}`} aria-label={formatLongDate(key)}>
            <h3 className={styles.weekHead}>
              <span>{names[i]}</span>
              <span className={styles.weekDate}>{formatDayMonth(key)}</span>
            </h3>
            <div className={styles.weekEntries}>
              {list.length === 0 && <span className={styles.none}>–</span>}
              {list.map((entry) => (
                <Card key={entry.key} entry={entry} />
              ))}
            </div>
          </section>
        )
      })}
    </div>
  )
}

function Card({ entry }: { entry: CalendarEntry }) {
  return (
    <Link to={entryLink(entry)} className={styles.card} data-type={entry.type} data-status={entry.status} data-unmonitored={entry.monitored ? undefined : 'true'}>
      <span className={styles.cardTitle}>{entry.title}</span>
      <span className={styles.cardMeta}>
        {entry.type === 'movie' ? releaseLabels[entry.releaseType] : `${episodeCode(entry)}${entry.episodeTitle ? ` · ${entry.episodeTitle}` : ''}`}
      </span>
      <span className={styles.cardStatus}>
        <span className={styles.statusDot} aria-hidden="true" />
        {statusLabels[entry.status]}
      </span>
    </Link>
  )
}

function AgendaView(props: { start: Date; end: Date; todayKey: string; byDay: Map<string, CalendarEntry[]>; loading: boolean }) {
  const days = daysBetween(props.start, props.end)
    .map(dayKey)
    .filter((key) => props.byDay.has(key))
  if (props.loading) return <p className={styles.note}>Loading…</p>
  if (days.length === 0) {
    return (
      <div className={styles.emptyAgenda}>
        <p className={styles.emptyTitle}>Nothing in these {daysBetween(props.start, props.end).length} days</p>
        <p className={styles.note}>Movies show on their cinema, digital and physical release, episodes on their air date.</p>
      </div>
    )
  }
  return (
    <div className={styles.agenda}>
      {days.map((key) => (
        <section key={key} className={styles.agendaDay} aria-label={formatLongDate(key)}>
          <h3 className={styles.dayHeading}>
            {formatLongDate(key)}
            {key === props.todayKey && <span className={styles.todayTag}>Today</span>}
          </h3>
          <EntryList entries={props.byDay.get(key) ?? []} />
        </section>
      ))}
    </div>
  )
}

function EntryList({ entries }: { entries: CalendarEntry[] }) {
  return (
    <ul className={styles.entryList}>
      {entries.map((entry) => (
        <li key={entry.key}>
          <Link to={entryLink(entry)} className={styles.row} data-type={entry.type} data-status={entry.status} data-unmonitored={entry.monitored ? undefined : 'true'}>
            <span className={styles.rowMain}>
              <span className={styles.rowTitle}>
                {entry.title}
                {entry.type === 'movie' && entry.year > 0 && <span className={styles.faint}> ({entry.year})</span>}
              </span>
              <span className={styles.rowMeta}>
                {entry.type === 'movie'
                  ? releaseLabels[entry.releaseType]
                  : `${episodeCode(entry)}${entry.episodeTitle ? ` · ${entry.episodeTitle}` : ''}${entry.network ? ` · ${entry.network}` : ''}`}
                {!entry.monitored && ' · Unmonitored'}
              </span>
            </span>
            <span className={styles.rowStatus}>
              <span className={styles.statusDot} aria-hidden="true" />
              {statusLabels[entry.status]}
            </span>
          </Link>
        </li>
      ))}
    </ul>
  )
}
