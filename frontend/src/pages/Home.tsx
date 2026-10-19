import { useQuery } from '@tanstack/react-query'
import { useMemo, useState, type CSSProperties, type ReactNode } from 'react'
import { Link } from 'react-router'
import { ApiError } from '../api/client'
import { downloadKeys, downloadsApi, withLive, type DownloadItem } from '../api/downloads'
import { libraryApi, queryKeys, type RootFolder } from '../api/library'
import type { OngoingNotice } from '../api/status'
import MigrationCard from '../migration/OverviewCard'
import { calendarApi, calendarKeys, wantedApi, wantedKeys, type CalendarEntry, type CalendarFilter } from '../api/wanted'
import { addDays, dayKey } from '../calendar/dates'
import Icon, { type IconName } from '../components/Icon'
import { Skeleton } from '../components/Loader'
import PageHeader from '../components/PageHeader'
import Poster from '../components/Poster'
import ui from '../components/ui.module.css'
import { formatBytes } from '../lib/format'
import { formatEta, formatSpeed } from '../status/format'
import { useLiveProgress, useOngoing } from '../status/useOngoing'
import styles from './Home.module.css'

/** Overview like CloudZH: four numbers, then what runs and what was added (left), what needs a look and what comes (right). */
export default function Home() {
  const stats = useQuery({ queryKey: queryKeys.stats, queryFn: libraryApi.stats })
  const wanted = useQuery({ queryKey: wantedKeys.summary, queryFn: wantedApi.summary })
  const roots = useQuery({ queryKey: queryKeys.rootFolders, queryFn: libraryApi.rootFolders, retry: false })
  const s = stats.data
  const w = wanted.data

  // Free space: every disk once (several root folders often share one).
  const free = useMemo(() => {
    const disks = new Map<string, number>()
    for (const r of roots.data ?? []) if (r.freeSpace != null && r.totalSpace != null) disks.set(`${r.totalSpace}`, r.freeSpace)
    return disks.size ? [...disks.values()].reduce((a, b) => a + b, 0) : null
  }, [roots.data])

  return (
    <div>
      <PageHeader title="Overview" lead="Your library at a glance." />
      {stats.isError && <p className={`${ui.errBox} ${styles.gap}`}>Backend not reachable: {stats.error.message}</p>}
      <MigrationCard />

      <div className={styles.ov}>
        <section className={styles.metrics} aria-label="Library">
          <Metric to="/library/movies" tone="var(--movies)" label="Movies" icon="film" value={s?.movies} sub={s && `${s.moviesWithFile} on disk`} />
          <Metric
            to="/library/series"
            tone="var(--series)"
            label="Series"
            icon="tv"
            value={s?.series}
            sub={s && `${s.episodesWithFile} of ${s.episodes} episodes on disk`}
          />
          <Metric
            to="/wanted/missing"
            tone="var(--accent)"
            label="Missing"
            icon="target"
            value={w && w.missingMovies + w.missingEpisodes}
            sub={w && `${w.missingMovies} ${w.missingMovies === 1 ? 'movie' : 'movies'} · ${w.missingEpisodes} ${w.missingEpisodes === 1 ? 'episode' : 'episodes'}`}
            tag={w && w.cutoffUnmetMovies + w.cutoffUnmetEpisodes > 0 ? `${w.cutoffUnmetMovies + w.cutoffUnmetEpisodes} below cutoff` : undefined}
          />
          <Metric
            tone="var(--ok)"
            label="Size on disk"
            icon="hdd"
            value={s && formatBytes(s.sizeOnDisk)}
            sub={free != null ? `${formatBytes(free)} free` : s && 'Movies and episodes'}
          />
        </section>

        <div className={styles.g2}>
          <div className={styles.stack}>
            <Downloads />
            <RecentlyAdded />
          </div>
          <div className={styles.stack}>
            <Attention />
            <ComingUp />
            {roots.data && roots.data.length > 0 && <DiskSpace roots={roots.data} />}
          </div>
        </div>
      </div>
    </div>
  )
}

function Metric(props: {
  to?: string
  tone: string
  label: string
  icon: IconName
  value: ReactNode
  sub: ReactNode
  tag?: string
}) {
  const body = (
    <>
      <div className={styles.mTop}>
        <span className={ui.eyebrow}>{props.label}</span>
        {props.tag ? <span className={`${ui.tag} ${styles.mTag}`}>{props.tag}</span> : <Icon name={props.icon} size={16} className={styles.mIcon} />}
      </div>
      <div className={styles.val}>{props.value ?? <span className={styles.valWait}>–</span>}</div>
      <div className={styles.sub}>{props.sub || ' '}</div>
    </>
  )
  const style = { '--tone': props.tone } as CSSProperties
  return props.to ? (
    <Link to={props.to} className={`${styles.metric} ${styles.metricLink}`} style={style}>
      {body}
    </Link>
  ) : (
    <div className={styles.metric} style={style}>
      {body}
    </div>
  )
}

function PanelHead({ icon, title, meta, children }: { icon?: IconName; title: string; meta?: ReactNode; children?: ReactNode }) {
  return (
    <div className={styles.ph}>
      {icon && <Icon name={icon} size={17} className={styles.phIcon} />}
      <h3>{title}</h3>
      {meta && <span className={styles.phMeta}>{meta}</span>}
      {children && <div className={styles.phRight}>{children}</div>}
    </div>
  )
}

/** "42.7 %": one decimal, rounded down so 100 % only shows when it is really done. */
function precise(value: number | null | undefined): string {
  const pct = Math.floor(Math.min(1, Math.max(0, value ?? 0)) * 1000) / 10
  return `${pct.toFixed(1)} %`
}

const stepLabels: Partial<Record<DownloadItem['status'], string>> = {
  queued: 'Queued',
  paused: 'Paused',
  verifying: 'Verifying',
  repairing: 'Repairing',
  extracting: 'Extracting',
}

const maxDownloads = 5

function Downloads() {
  // Same query as the top bar and Activity; the hub ("downloadChanged") refreshes it, the interval is a safety net.
  const queue = useQuery({
    queryKey: downloadKeys.queue,
    queryFn: downloadsApi.queue,
    refetchInterval: (q) => (q.state.data?.items.length ? 10_000 : 60_000),
    retry: (count, error) => !(error instanceof ApiError && error.status === 404) && count < 1,
  })
  const live = useLiveProgress()
  if (queue.isError) return null

  const items = (queue.data?.items ?? []).map((i) => withLive(i, live, queue.dataUpdatedAt))
  const speed = items.reduce((sum, i) => sum + (i.status === 'downloading' ? i.speedBps : 0), 0)

  return (
    <section className={ui.panel} aria-label="Downloading now">
      <PanelHead
        title="Downloading now"
        meta={items.length > 0 && `${items.length} in the queue${speed > 0 ? ` · ${formatSpeed(speed)}` : ''}${queue.data?.paused ? ' · paused' : ''}`}
      >
        <Link className={`${ui.ghost} ${ui.sm}`} to="/activity">
          Activity
        </Link>
      </PanelHead>
      {!queue.data ? (
        <Skeleton rows={3} />
      ) : items.length === 0 ? (
        <div className={styles.idle}>
          <Icon name="download" size={18} />
          <span>Nothing is downloading right now.</span>
        </div>
      ) : (
        <div className={styles.dlList}>
          {items.slice(0, maxDownloads).map((item) => (
            <DownloadRow key={item.id} item={item} />
          ))}
          {items.length > maxDownloads && (
            <Link className={styles.more} to="/activity">
              {items.length - maxDownloads === 1 ? 'and 1 more download' : `and ${items.length - maxDownloads} more downloads`}
            </Link>
          )}
        </div>
      )}
    </section>
  )
}

function DownloadRow({ item }: { item: DownloadItem }) {
  const post = item.status === 'verifying' || item.status === 'repairing' || item.status === 'extracting'
  const progress = post ? (item.stepProgress ?? 0) : item.progress
  const step = stepLabels[item.status]
  const running = item.status === 'downloading' && item.speedBps > 0
  const link = item.movieId ? `/library/movies/${item.movieId}` : item.seriesId ? `/library/series/${item.seriesId}` : '/activity'
  return (
    <div className={styles.dl}>
      <div className={styles.dlTop}>
        <Icon name={item.category === 'series' ? 'tv' : item.category === 'movie' ? 'film' : 'download'} size={15} className={styles.dlIcon} />
        <Link to={link} className={styles.dlTitle} title={item.title}>
          {item.title}
        </Link>
        <span className={styles.dlPct}>{precise(progress)}</span>
      </div>
      <div
        className={`${styles.bar} ${item.status === 'paused' || item.status === 'queued' ? styles.barIdle : post ? styles.barPost : ''}`}
        role="progressbar"
        aria-label={`${item.title} progress`}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={Math.floor(progress * 100)}
      >
        <i style={{ width: `${Math.min(100, Math.max(0, progress * 100))}%` }} />
      </div>
      <div className={styles.dlMeta}>
        {step && <span className={styles.dlStep}>{item.statusMessage && post ? item.statusMessage : step}</span>}
        {!post && <span>{`${formatBytes(item.downloadedBytes)} of ${formatBytes(item.totalBytes)}`}</span>}
        {running && <span>{formatSpeed(item.speedBps)}</span>}
        {running && item.etaSeconds != null && <span className={styles.eta}>{formatEta(item.etaSeconds)}</span>}
        {item.status === 'downloading' && item.speedBps <= 0 && <span>Waiting for connections</span>}
      </div>
    </div>
  )
}

const recentMax = 8

function RecentlyAdded() {
  const movies = useQuery({ queryKey: queryKeys.movies, queryFn: libraryApi.movies })
  const series = useQuery({ queryKey: queryKeys.series, queryFn: libraryApi.series })
  const recent = useMemo(() => {
    const all = [
      ...(movies.data ?? []).map((m) => ({ key: `m${m.id}`, to: `/library/movies/${m.id}`, title: m.title, year: m.year, poster: m.posterPath, added: m.added, kind: 'Movie' })),
      ...(series.data ?? []).map((s) => ({ key: `s${s.id}`, to: `/library/series/${s.id}`, title: s.title, year: s.year, poster: s.posterPath, added: s.added, kind: 'Series' })),
    ]
    return all.sort((a, b) => b.added.localeCompare(a.added)).slice(0, recentMax)
  }, [movies.data, series.data])
  const loading = !movies.data && !series.data && !movies.isError

  return (
    <section className={ui.panel} aria-label="Recently added">
      <PanelHead title="Recently added">
        <Link className={`${ui.ghost} ${ui.sm}`} to="/library/movies">
          Library
        </Link>
      </PanelHead>
      {loading ? (
        <Skeleton rows={2} />
      ) : recent.length === 0 ? (
        <div className={styles.idle}>
          <Icon name="film" size={18} />
          <span>Nothing added yet.</span>
          <Link className={`${ui.button} ${ui.sm} ${styles.idleAction}`} to="/library/import">
            Import a library
          </Link>
        </div>
      ) : (
        <div className={styles.posters}>
          {recent.map((r) => (
            <Link key={r.key} to={r.to} className={styles.pcard} title={r.title}>
              <Poster path={r.poster} title={r.title} size="w185" />
              <span className={styles.pTitle}>{r.title}</span>
              <span className={styles.pMeta}>
                {r.year > 0 ? `${r.year} · ` : ''}
                {r.kind}
              </span>
            </Link>
          ))}
        </div>
      )}
    </section>
  )
}

const levelRank = { error: 0, warn: 1, info: 2 } as const

function Attention() {
  const { admin, data, isError } = useOngoing()
  if (!admin || isError) return null
  if (!data) {
    return (
      <div className={`${ui.panel} ${styles.attWait} ${styles.first}`}>
        <Skeleton rows={2} />
      </div>
    )
  }

  const notices = [...data.notices].sort((a, b) => levelRank[a.level] - levelRank[b.level])
  const problems = notices.filter((n) => n.level !== 'info')
  if (notices.length === 0) {
    return (
      <div className={`${styles.attOk} ${styles.first}`}>
        <Icon name="check" size={18} />
        <span>
          <b>Everything is fine.</b> No warnings right now.
        </span>
        <Link className={`${ui.ghost} ${ui.sm}`} to="/system/status">
          Status
        </Link>
      </div>
    )
  }
  const hasError = notices.some((n) => n.level === 'error')
  const max = 5
  return (
    <section className={`${ui.panel} ${styles.first} ${problems.length ? (hasError ? styles.attErr : styles.attWarn) : ''}`} aria-label="Needs attention">
      <PanelHead icon={problems.length ? 'alert' : 'info'} title={problems.length ? 'Needs attention' : 'Notices'} meta={notices.length === 1 ? '1 item' : `${notices.length} items`} />
      <div className={styles.attList}>
        {notices.slice(0, max).map((n) => (
          <NoticeItem key={n.key} notice={n} />
        ))}
      </div>
      {notices.length > max && <p className={styles.moreText}>{`Plus ${notices.length - max} more in the status window.`}</p>}
    </section>
  )
}

function NoticeItem({ notice }: { notice: OngoingNotice }) {
  return (
    <div className={styles.attItem} data-level={notice.level}>
      <span className={styles.attIc}>
        <Icon name={notice.level === 'info' ? 'info' : 'alert'} size={15} />
      </span>
      <div className={styles.grow}>
        <b>{notice.title}</b>
        {notice.sub && <div className={styles.attSub}>{notice.sub}</div>}
      </div>
      {notice.link && (
        <Link className={`${ui.button} ${ui.sm}`} to={notice.link}>
          Open
        </Link>
      )}
    </div>
  )
}

const comingMax = 6

function ComingUp() {
  const [today] = useState(() => new Date())
  const week: CalendarFilter = { start: dayKey(today), end: dayKey(addDays(today, 6)), unmonitored: false, type: 'all' }
  const upcoming = useQuery({ queryKey: calendarKeys.range(week), queryFn: () => calendarApi.entries(week) })
  const entries = [...(upcoming.data ?? [])].sort((a, b) => a.date.localeCompare(b.date))
  const todayKey = dayKey(today)
  const tomorrowKey = dayKey(addDays(today, 1))

  return (
    <section className={ui.panel} aria-label="Coming up">
      <PanelHead title="Coming up" meta="Next 7 days">
        <Link className={`${ui.ghost} ${ui.sm}`} to="/calendar?view=agenda">
          Calendar
        </Link>
      </PanelHead>
      {upcoming.isError ? (
        <p className={styles.mutedText}>{upcoming.error.message}</p>
      ) : !upcoming.data ? (
        <Skeleton rows={3} />
      ) : entries.length === 0 ? (
        <p className={styles.mutedText}>Nothing releases or airs in the next 7 days.</p>
      ) : (
        <ul className={styles.coming}>
          {entries.slice(0, comingMax).map((e) => (
            <li key={e.key}>
              <ComingRow entry={e} label={e.date === todayKey ? 'Today' : e.date === tomorrowKey ? 'Tomorrow' : null} />
            </li>
          ))}
          {entries.length > comingMax && (
            <li>
              <Link className={styles.more} to="/calendar?view=agenda">
                {`${entries.length - comingMax} more in the calendar`}
              </Link>
            </li>
          )}
        </ul>
      )}
    </section>
  )
}

const releaseLabels = { inCinemas: 'In cinemas', digital: 'Digital release', physical: 'Physical release', airDate: '' } as const

function ComingRow({ entry: e, label }: { entry: CalendarEntry; label: string | null }) {
  const date = new Date(`${e.date}T00:00:00`)
  const detail =
    e.type === 'movie'
      ? releaseLabels[e.releaseType]
      : `${e.seasonNumber}x${String(e.episodeNumber).padStart(2, '0')}${e.episodeTitle ? ` · ${e.episodeTitle}` : ''}`
  return (
    <Link to={e.type === 'movie' ? `/library/movies/${e.movieId}` : `/library/series/${e.seriesId}`} className={styles.cRow} data-type={e.type}>
      <span className={`${styles.cDate} ${label === 'Today' ? styles.cToday : ''}`} aria-label={date.toDateString()}>
        <span>{label === 'Today' ? 'Today' : date.toLocaleDateString('en-US', { weekday: 'short' })}</span>
        <b>{date.getDate()}</b>
      </span>
      <span className={styles.grow}>
        <span className={styles.cTitle}>{e.title}</span>
        <span className={styles.cMeta}>{detail}</span>
      </span>
      {e.hasFile && (
        <span className={styles.cHave} title="Already on disk">
          <Icon name="check" size={14} />
        </span>
      )}
    </Link>
  )
}

function DiskSpace({ roots }: { roots: RootFolder[] }) {
  return (
    <section className={ui.panel} aria-label="Disk space">
      <PanelHead title="Disk space">
        <Link className={`${ui.ghost} ${ui.sm}`} to="/settings/media-management">
          Root folders
        </Link>
      </PanelHead>
      <div className={styles.disks}>
        {roots.map((r) => {
          const total = r.totalSpace ?? 0
          const known = r.freeSpace != null && total > 0
          const used = known ? (total - (r.freeSpace ?? 0)) / total : 0
          const tone = !r.accessible || used > 0.95 ? styles.barErr : used > 0.85 ? styles.barWarn : ''
          return (
            <div key={r.id} className={styles.disk}>
              <div className={styles.diskTop}>
                <Icon name={r.mediaType === 'Movie' ? 'film' : 'tv'} size={14} className={styles.dlIcon} />
                <span className={styles.diskPath} title={r.path}>
                  {`\u200e${r.path}\u200e`}
                </span>
                <span className={styles.dlPct}>{known ? `${Math.round(used * 100)} %` : ''}</span>
              </div>
              <div className={`${styles.bar} ${tone}`}>
                <i style={{ width: `${used * 100}%` }} />
              </div>
              <div className={styles.dlMeta}>
                {!r.accessible ? (
                  <span className={styles.errText}>Not reachable</span>
                ) : known ? (
                  <span>{`${formatBytes(r.freeSpace)} free of ${formatBytes(r.totalSpace)}`}</span>
                ) : (
                  <span>Size unknown</span>
                )}
                {r.writeProblem && <span className={styles.errText}>{r.writeProblem}</span>}
              </div>
            </div>
          )
        })}
      </div>
    </section>
  )
}
