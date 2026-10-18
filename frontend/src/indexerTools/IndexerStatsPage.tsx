import { useQuery } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { Link, useSearchParams } from 'react-router'
import { indexerActivityApi, indexerActivityKeys, type IndexerStats, type IndexerStatsRow } from '../api/indexerActivity'
import Icon from '../components/Icon'
import Loader from '../components/Loader'
import PageHeader from '../components/PageHeader'
import ui from '../components/ui.module.css'
import { formatDayMonth } from '../lib/format'
import styles from './IndexerTools.module.css'

const ranges = [7, 30, 90] as const
type Range = (typeof ranges)[number]

const number = new Intl.NumberFormat()

function formatMs(ms: number): string {
  if (!ms) return '–'
  return ms >= 10_000 ? `${(ms / 1000).toFixed(1)} s` : `${number.format(Math.round(ms))} ms`
}

function percent(part: number, total: number): string {
  if (total === 0) return '0%'
  const value = (part / total) * 100
  return value > 0 && value < 1 ? '<1%' : `${Math.round(value)}%`
}

/** Every day from `from` to `to` (inclusive), with zeros where the server sent nothing. */
function fillDays(stats: IndexerStats, days: number): { date: string; queries: number; grabs: number }[] {
  const byDate = new Map(stats.daily.map((d) => [d.date.slice(0, 10), d]))
  const end = new Date(`${(stats.to || new Date().toISOString()).slice(0, 10)}T00:00:00Z`)
  const result = []
  for (let i = days - 1; i >= 0; i--) {
    const date = new Date(end.getTime() - i * 86_400_000).toISOString().slice(0, 10)
    const day = byDate.get(date)
    result.push({ date, queries: day?.queries ?? 0, grabs: day?.grabs ?? 0 })
  }
  return result
}

function shortDate(date: string): string {
  return formatDayMonth(date)
}

/** How often each indexer was asked, how often it delivered and how fast it answers (like Prowlarr's Stats). */
export default function IndexerStatsPage() {
  const [url, setUrl] = useSearchParams()
  const days: Range = ranges.find((r) => String(r) === url.get('days')) ?? 30
  const stats = useQuery({
    queryKey: indexerActivityKeys.stats(days),
    queryFn: () => indexerActivityApi.stats(days),
    placeholderData: (previous) => previous,
  })

  const data = stats.data
  const rows = useMemo(
    () => [...(data?.indexers ?? [])].sort((a, b) => b.queries + b.rssQueries - (a.queries + a.rssQueries) || a.indexer.localeCompare(b.indexer)),
    [data],
  )
  const totals = useMemo(() => {
    const t = { queries: 0, rss: 0, grabs: 0, failed: 0, calls: 0, weightedMs: 0 }
    for (const r of rows) {
      const calls = r.queries + r.rssQueries
      t.queries += r.queries
      t.rss += r.rssQueries
      t.grabs += r.grabs
      t.failed += r.failedQueries + r.failedGrabs
      t.calls += calls
      t.weightedMs += r.averageResponseMs * calls
    }
    return t
  }, [rows])
  const daily = useMemo(() => (data ? fillDays(data, days) : []), [data, days])

  return (
    <div className={styles.page}>
      <PageHeader
        title="Stats"
        lead="How often StreamZH asked each indexer, what it grabbed and how fast the indexers answer."
        actions={
          <div className={ui.seg} role="radiogroup" aria-label="Time range">
            {ranges.map((r) => (
              <button
                key={r}
                type="button"
                role="radio"
                aria-checked={days === r}
                className={days === r ? ui.segOn : undefined}
                onClick={() => setUrl(r === 30 ? {} : { days: String(r) }, { replace: true })}
              >
                {r} days
              </button>
            ))}
          </div>
        }
      />

      {stats.isError ? (
        <div className={ui.panel}>
          <div className={ui.empty}>
            <h3>The stats could not be loaded</h3>
            <p className={ui.error}>{stats.error.message}</p>
            <button className={`${ui.button} ${ui.sm}`} type="button" onClick={() => void stats.refetch()}>
              Try again
            </button>
          </div>
        </div>
      ) : !data ? (
        <Loader text="Loading the stats" />
      ) : rows.length === 0 ? (
        <div className={ui.panel}>
          <div className={ui.empty}>
            <span className={styles.emptyIcon}>
              <Icon name="gauge" size={20} />
            </span>
            <h3>No indexer activity yet</h3>
            <p>Stats appear once StreamZH has searched or grabbed something. Add an indexer, or run a search.</p>
            <div className={styles.emptyActions}>
              <Link className={`${ui.primary} ${ui.sm}`} to="/indexers">
                Indexers
              </Link>
              <Link className={`${ui.button} ${ui.sm}`} to="/indexers/search">
                Search
              </Link>
            </div>
          </div>
        </div>
      ) : (
        <div className={`${styles.statsBody} ${stats.isPlaceholderData ? styles.stale : ''}`}>
          <section className={styles.kpis} aria-label="Totals">
            <Kpi label="Queries" value={number.format(totals.queries + totals.rss)} sub={`${number.format(totals.rss)} of them RSS`} />
            <Kpi label="Grabs" value={number.format(totals.grabs)} sub={`${percent(totals.grabs, totals.queries)} of searches`} />
            <Kpi
              label="Failed"
              value={number.format(totals.failed)}
              sub={`${percent(totals.failed, totals.calls + totals.grabs)} of all calls`}
              tone={totals.failed > 0 ? 'warn' : undefined}
            />
            <Kpi label="Avg response" value={formatMs(totals.calls ? totals.weightedMs / totals.calls : 0)} sub="Weighted by queries" />
          </section>

          <div className={styles.chartGrid}>
            <BarList
              title="Queries per indexer"
              rows={rows}
              value={(r) => r.queries + r.rssQueries}
              detail={(r) => `${number.format(r.queries)} searches, ${number.format(r.rssQueries)} RSS`}
              tone="queries"
            />
            <BarList title="Grabs per indexer" rows={rows} value={(r) => r.grabs} detail={(r) => `${number.format(r.failedGrabs)} failed`} tone="grabs" />
            <DailyBars title="Queries per day" days={daily} value={(d) => d.queries} unit="queries" tone="queries" />
            <DailyBars title="Grabs per day" days={daily} value={(d) => d.grabs} unit="grabs" tone="grabs" />
          </div>

          <section className={ui.panel}>
            <div className={ui.panelHead}>
              <h2>Indexers</h2>
            </div>
            <div className={styles.tableScroll}>
              <table className={`${ui.table} ${styles.statsTable}`}>
                <thead>
                  <tr>
                    <th scope="col">Indexer</th>
                    <th scope="col">Queries</th>
                    <th scope="col">RSS</th>
                    <th scope="col">Grabs</th>
                    <th scope="col">Failed</th>
                    <th scope="col">Avg response</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((r) => {
                    const failed = r.failedQueries + r.failedGrabs
                    const calls = r.queries + r.rssQueries + r.grabs
                    return (
                      <tr key={r.indexerId}>
                        <th scope="row">{r.indexer}</th>
                        <td>{number.format(r.queries)}</td>
                        <td>{number.format(r.rssQueries)}</td>
                        <td>{number.format(r.grabs)}</td>
                        <td
                          className={failed > 0 ? styles.failedCell : undefined}
                          title={`${r.failedQueries} failed queries, ${r.failedGrabs} failed grabs`}
                        >
                          {number.format(failed)}
                          {failed > 0 && <span className={styles.rate}> {percent(failed, calls)}</span>}
                        </td>
                        <td>{formatMs(r.averageResponseMs)}</td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
          </section>
        </div>
      )}
    </div>
  )
}

function Kpi({ label, value, sub, tone }: { label: string; value: string; sub: string; tone?: 'warn' }) {
  return (
    <div className={`${ui.panel} ${styles.metric}`}>
      <div className={styles.metricLabel}>{label}</div>
      <div className={`${styles.metricValue} ${tone === 'warn' ? styles.warnValue : ''}`}>{value}</div>
      <div className={styles.metricSub}>{sub}</div>
    </div>
  )
}

/** Horizontal bars: one per indexer, longest first, value labelled at the end. */
function BarList({
  title,
  rows,
  value,
  detail,
  tone,
}: {
  title: string
  rows: IndexerStatsRow[]
  value: (row: IndexerStatsRow) => number
  detail: (row: IndexerStatsRow) => string
  tone: 'queries' | 'grabs'
}) {
  const sorted = [...rows].sort((a, b) => value(b) - value(a) || a.indexer.localeCompare(b.indexer))
  const max = Math.max(1, ...sorted.map(value))
  return (
    <section className={`${ui.panel} ${styles.chart}`}>
      <h2 className={styles.chartTitle}>{title}</h2>
      <ul className={styles.barList}>
        {sorted.map((row) => {
          const v = value(row)
          return (
            <li key={row.indexerId} className={styles.barItem} title={`${row.indexer}: ${number.format(v)} (${detail(row)})`}>
              <span className={styles.barName}>{row.indexer}</span>
              <span className={styles.barTrack}>
                <span className={`${styles.bar} ${styles[tone]}`} style={{ width: v === 0 ? 0 : `max(4px, ${(v / max) * 100}%)` }} />
              </span>
              <span className={styles.barValue}>{number.format(v)}</span>
            </li>
          )
        })}
      </ul>
    </section>
  )
}

type Day = { date: string; queries: number; grabs: number }

/** Columns per day; hovering or focusing a day shows its value in the header line. */
function DailyBars({
  title,
  days,
  value,
  unit,
  tone,
}: {
  title: string
  days: Day[]
  value: (day: Day) => number
  unit: string
  tone: 'queries' | 'grabs'
}) {
  const [active, setActive] = useState<number | null>(null)
  const values = days.map(value)
  const max = Math.max(...values, 0)
  const total = values.reduce((a, b) => a + b, 0)
  const scale = niceMax(max)
  const shown = active != null ? days[active] : null

  return (
    <section className={`${ui.panel} ${styles.chart}`}>
      <div className={styles.chartHead}>
        <h2 className={styles.chartTitle}>{title}</h2>
        <span className={styles.readout} aria-live="polite">
          {shown ? (
            <>
              {shortDate(shown.date)} · <strong>{number.format(value(shown))}</strong> {unit}
            </>
          ) : (
            <>
              <strong>{number.format(total)}</strong> in {days.length} days
            </>
          )}
        </span>
      </div>
      <div className={styles.daily} onMouseLeave={() => setActive(null)}>
        <span className={styles.axisTop}>{number.format(scale)}</span>
        <div className={`${styles.columns} ${days.length > 45 ? styles.columnsDense : ''}`}>
          {days.map((day, i) => {
            const v = values[i]
            return (
              <span
                key={day.date}
                className={`${styles.column} ${active === i ? styles.columnActive : ''}`}
                tabIndex={0}
                role="img"
                aria-label={`${shortDate(day.date)}: ${v} ${unit}`}
                onMouseEnter={() => setActive(i)}
                onFocus={() => setActive(i)}
                onBlur={() => setActive(null)}
              >
                <span className={`${styles.columnBar} ${styles[tone]}`} style={{ height: v === 0 ? 0 : `max(2px, ${(v / scale) * 100}%)` }} />
              </span>
            )
          })}
        </div>
        <div className={styles.axisDates} aria-hidden="true">
          <span>{days.length > 0 && shortDate(days[0].date)}</span>
          <span>{days.length > 0 && shortDate(days[days.length - 1].date)}</span>
        </div>
      </div>
    </section>
  )
}

/** Rounds the top of the scale up to 1, 2 or 5 × 10ⁿ so the axis label is a clean number. */
function niceMax(max: number): number {
  if (max <= 0) return 1
  const power = 10 ** Math.floor(Math.log10(max))
  const step = [1, 2, 5, 10].find((s) => s * power >= max) ?? 10
  return step * power
}
