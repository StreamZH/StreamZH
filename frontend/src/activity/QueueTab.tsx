import { useQuery } from '@tanstack/react-query'
import { useState, type ReactNode } from 'react'
import { Link } from 'react-router'
import { ApiError } from '../api/client'
import { downloadKeys, downloadsApi, withLive, type DownloadItem } from '../api/downloads'
import { mediaManagementApi, mediaManagementKeys } from '../api/mediaManagement'
import ui from '../components/ui.module.css'
import Toasts from '../indexerTools/Toast'
import { formatBytes } from '../lib/format'
import { formatEta, formatSpeed } from '../status/format'
import StatusIcon, { type StatusIconName } from '../status/icons'
import { useIsAdmin, useLiveProgress, useOngoing } from '../status/useOngoing'
import DownloadCard, { type DownloadActions } from './DownloadCard'
import { RemoveDialog, UploadDialog } from './Dialogs'
import { downloadTypeOptions, matchesSearch, matchesType, pick, useCustomCategories } from './filterOptions'
import { FilterBar, FilterSelect, NoMatches, SearchBox } from './Filters'
import { useDownloadAction, useMediaLinks, useToasts } from './hooks'
import { PendingImports } from './imports'
import { useClampPage, useListParams } from './listParams'
import Pager from './Pager'
import { usePageSize } from './paging'
import styles from './Activity.module.css'

const lowSpace = 10 * 1024 ** 3

const filterKeys = ['q', 'status', 'type'] as const

const statusOptions = [
  { value: '', label: 'All statuses' },
  { value: 'downloading', label: 'Downloading' },
  { value: 'queued', label: 'Queued' },
  { value: 'paused', label: 'Paused' },
  { value: 'processing', label: 'Verifying, repairing, extracting' },
  { value: 'import', label: 'Waiting for import' },
]

/** True when the download is in the status filter ('import' only matches the "Waiting for import" section). */
function matchesStatus(item: DownloadItem, status: string): boolean {
  switch (status) {
    case '':
      return true
    case 'processing':
      return item.status === 'verifying' || item.status === 'repairing' || item.status === 'extracting'
    case 'import':
      return false
    default:
      return item.status === status
  }
}

/** Active downloads with live progress, the queue totals, and the actions. */
export default function QueueTab() {
  const admin = useIsAdmin()
  const queue = useQuery({
    queryKey: downloadKeys.queue,
    queryFn: downloadsApi.queue,
    refetchInterval: (q) => (q.state.data?.items.length ? 10_000 : 30_000),
    retry: (count, error) => !(error instanceof ApiError && error.status === 404) && count < 1,
  })
  const settings = useQuery({ queryKey: downloadKeys.settings, queryFn: downloadsApi.settings, enabled: admin, retry: false })
  const ongoing = useOngoing()
  const live = useLiveProgress()
  const { toasts, push, dismiss } = useToasts()
  const [removing, setRemoving] = useState<DownloadItem | null>(null)
  const [uploading, setUploading] = useState(false)

  const items = (queue.data?.items ?? []).map((i) => withLive(i, live, queue.dataUpdatedAt))
  const media = useMediaLinks(items)

  // Filters and paging (client-side: the queue is loaded as a whole).
  const { page, get, setFilters, setSearch, setPage, clear, active } = useListParams(filterKeys)
  const [pageSize, setPageSize] = usePageSize('activity-queue')
  const categories = useCustomCategories()
  const status = pick(get('status'), statusOptions)
  const search = get('q')
  const type = get('type')
  const matches = (item: DownloadItem) => matchesSearch(item.title, search) && matchesType(item, type)
  const filtered = items.filter((i) => matches(i) && matchesStatus(i, status))
  const shownPage = Math.min(page, Math.max(1, Math.ceil(filtered.length / pageSize)))
  const pageItems = filtered.slice((shownPage - 1) * pageSize, shownPage * pageSize)
  const pending = useQuery({ queryKey: mediaManagementKeys.pending, queryFn: mediaManagementApi.pending, enabled: false })
  const pendingMatches = status === '' || status === 'import' ? (pending.data ?? []).filter((p) => matches(p.download)).length : 0
  useClampPage(page, queue.data ? filtered.length : undefined, pageSize, setPage)
  const fail = (title: string) => (message: string) => push('error', title, message)

  const pause = useDownloadAction(downloadsApi.pause, fail('Could not pause'))
  const resume = useDownloadAction(downloadsApi.resume, fail('Could not resume'))
  const retry = useDownloadAction(downloadsApi.retry, fail('Could not retry'))
  const priority = useDownloadAction(({ id, value }: { id: number; value: number }) => downloadsApi.update(id, { priority: value }), fail('Could not change the priority'))
  const remove = useDownloadAction(
    ({ id, deleteFiles }: { id: number; deleteFiles: boolean }) => downloadsApi.remove(id, deleteFiles),
    fail('Could not remove'),
    () => {
      setRemoving(null)
      push('ok', 'Download removed')
    },
  )
  const pauseAll = useDownloadAction(downloadsApi.pauseAll, fail('Could not pause the queue'))
  const resumeAll = useDownloadAction(downloadsApi.resumeAll, fail('Could not resume the queue'))

  const actions: DownloadActions | null = admin
    ? {
        pause: (id) => pause.mutate(id),
        resume: (id) => resume.mutate(id),
        retry: (id) => retry.mutate(id),
        priority: (id, value) => priority.mutate({ id, value }),
        remove: (item) => setRemoving(item),
      }
    : null
  const pendingId = (m: { isPending: boolean; variables?: unknown }) =>
    m.isPending ? (typeof m.variables === 'number' ? m.variables : (m.variables as { id?: number } | undefined)?.id) : undefined
  const busyIds = new Set([pause, resume, retry, priority].map(pendingId).filter((id) => id != null))

  const blackhole = settings.data?.client === 'blackhole'
  const noServer = ongoing.data?.notices.some((n) => n.key === 'no-usenet-server') ?? false
  const data = queue.data
  const liveNewer = live && live.at > queue.dataUpdatedAt
  const speed = liveNewer ? Object.values(live.items).reduce((sum, i) => sum + (i.speedBps || 0), 0) : (data?.speedBps ?? 0)
  const remaining = items.reduce((sum, i) => sum + Math.max(0, i.totalBytes - i.downloadedBytes), 0)
  const eta = speed > 0 ? remaining / speed : null

  const header = data && (
    <div className={styles.summary}>
      <Stat icon="gauge" label="Speed" value={data.paused ? 'Paused' : formatSpeed(speed)} sub={data.speedLimitKBps > 0 ? `Limit ${formatSpeed(data.speedLimitKBps * 1024)}` : 'No speed limit'} />
      <Stat
        icon="download"
        label="Remaining"
        value={formatBytes(remaining)}
        sub={[`${items.length} ${items.length === 1 ? 'download' : 'downloads'}`, eta ? formatEta(eta) : ''].filter(Boolean).join(' · ')}
      />
      <Stat
        icon="disk"
        label="Free space"
        value={data.freeSpaceBytes != null ? formatBytes(data.freeSpaceBytes) : '–'}
        sub={data.freeSpaceBytes != null && data.freeSpaceBytes < lowSpace ? 'Low: downloads may stop' : 'Download folder'}
        tone={data.freeSpaceBytes != null && data.freeSpaceBytes < lowSpace ? 'warn' : undefined}
      />
      {admin && (
        <div className={styles.summaryActions}>
          {data.paused ? (
            <button type="button" className={ui.primary} onClick={() => resumeAll.mutate()} disabled={resumeAll.isPending}>
              <StatusIcon name="play" size={14} /> Resume all
            </button>
          ) : (
            <button type="button" className={ui.button} onClick={() => pauseAll.mutate()} disabled={pauseAll.isPending || items.length === 0}>
              <StatusIcon name="pause" size={14} /> Pause all
            </button>
          )}
          <button type="button" className={ui.button} onClick={() => setUploading(true)}>
            <StatusIcon name="upload" size={14} /> Add NZB
          </button>
        </div>
      )}
    </div>
  )

  let body: ReactNode
  if (blackhole) {
    body = (
      <Empty icon="folder" title="Downloads go to a watched folder">
        <p>
          StreamZH hands every grabbed NZB to the folder <span className={ui.mono}>{settings.data?.nzbFolder}</span>. SABnzbd, NZBGet or
          another downloader picks it up from there, so nothing shows up in this queue.
        </p>
        <div className={styles.emptyActions}>
          <Link className={ui.button} to="/settings/media-management">
            Settings → Downloads
          </Link>
          <Link className={ui.button} to="/activity?tab=history">
            Grab history
          </Link>
        </div>
      </Empty>
    )
  } else if (queue.isError) {
    const missing = queue.error instanceof ApiError && queue.error.status === 404
    body = (
      <Empty icon="error" title={missing ? 'The download engine is not available' : 'The queue could not be loaded'}>
        <p>{missing ? 'This StreamZH build has no built-in downloader yet.' : queue.error.message}</p>
        <div className={styles.emptyActions}>
          <button type="button" className={ui.button} onClick={() => void queue.refetch()}>
            Try again
          </button>
        </div>
      </Empty>
    )
  } else if (!data) {
    body = (
      <div className={styles.loading} role="status">
        <span className={styles.bigSpinner} aria-hidden="true" />
        Loading the queue…
      </div>
    )
  } else if (items.length === 0 && !active) {
    body = noServer ? (
      <Empty icon="server" title="Add your Usenet server first">
        <p>StreamZH downloads releases itself. It needs the address and login of your Usenet provider before anything can start.</p>
        <div className={styles.emptyActions}>
          <Link className={ui.primary} to="/settings/usenet">
            Settings → Usenet servers
          </Link>
        </div>
      </Empty>
    ) : (
      <Empty icon="download" title="Nothing is downloading">
        <p>Grabbed releases appear here with their progress, from download to repair and unpack. Search a movie or episode, or add an NZB file yourself.</p>
        <div className={styles.emptyActions}>
          <Link className={ui.button} to="/library/movies">
            Movies
          </Link>
          <Link className={ui.button} to="/library/series">
            Series
          </Link>
          {admin && (
            <button type="button" className={ui.button} onClick={() => setUploading(true)}>
              <StatusIcon name="upload" size={14} /> Add NZB
            </button>
          )}
        </div>
      </Empty>
    )
  } else {
    body = (
      <>
        {noServer && (
          <p className={styles.banner}>
            <StatusIcon name="warn" size={15} />
            <span>
              No Usenet server is configured, so these downloads cannot start. <Link to="/settings/usenet">Add a server</Link>
            </span>
          </p>
        )}
        <FilterBar label="Filter the queue" active={active} matches={filtered.length + pendingMatches} noun="downloads" onClear={clear}>
          <SearchBox value={search} onChange={setSearch} placeholder="Search titles" label="Search the queue" />
          <FilterSelect label="Status" value={status} onChange={(v) => setFilters({ status: v })} options={statusOptions} />
          <FilterSelect label="Type" value={type} onChange={(v) => setFilters({ type: v })} options={downloadTypeOptions} groups={[{ label: 'Categories', options: categories }]} />
        </FilterBar>
        {active && filtered.length + pendingMatches === 0 && <NoMatches noun="downloads" onClear={clear} />}
        {pageItems.length > 0 && (
          <div className={styles.cards}>
            {pageItems.map((item) => (
              <DownloadCard key={item.id} item={item} media={media(item)} actions={actions} busy={busyIds.has(item.id)} />
            ))}
          </div>
        )}
        {status !== 'import' && <Pager page={page} pageSize={pageSize} total={filtered.length} noun="downloads" onPage={setPage} onPageSize={setPageSize} />}
      </>
    )
  }

  return (
    <>
      {!blackhole && header}
      {body}
      {!blackhole && (status === '' || status === 'import') && <PendingImports admin={admin} onToast={push} match={active ? matches : undefined} />}
      {removing && (
        <RemoveDialog
          item={removing}
          busy={remove.isPending}
          onClose={() => setRemoving(null)}
          onConfirm={(deleteFiles) => remove.mutate({ id: removing.id, deleteFiles })}
        />
      )}
      {uploading && (
        <UploadDialog
          onClose={() => setUploading(false)}
          onUploaded={(title) => {
            setUploading(false)
            push('ok', 'Added to the queue', title)
            void queue.refetch()
          }}
        />
      )}
      <Toasts toasts={toasts} onDismiss={dismiss} />
    </>
  )
}

function Stat({ icon, label, value, sub, tone }: { icon: StatusIconName; label: string; value: string; sub: string; tone?: 'warn' }) {
  return (
    <div className={`${styles.stat} ${tone === 'warn' ? styles.statWarn : ''}`}>
      <span className={styles.statIcon}>
        <StatusIcon name={icon} size={16} />
      </span>
      <div className={styles.statText}>
        <span className={styles.statLabel}>{label}</span>
        <span className={styles.statValue}>{value}</span>
        <span className={styles.statSub}>{sub}</span>
      </div>
    </div>
  )
}

export function Empty({ icon, title, children }: { icon: StatusIconName; title: string; children: ReactNode }) {
  return (
    <div className={styles.empty}>
      <span className={styles.emptyIcon}>
        <StatusIcon name={icon} size={22} />
      </span>
      <p className={styles.emptyTitle}>{title}</p>
      {children}
    </div>
  )
}
