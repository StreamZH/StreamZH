import { useQuery } from '@tanstack/react-query'
import { useState, type ReactNode } from 'react'
import { Link } from 'react-router'
import { ApiError } from '../api/client'
import { downloadKeys, downloadsApi, withLive, type DownloadItem } from '../api/downloads'
import { mediaManagementApi, mediaManagementKeys } from '../api/mediaManagement'
import ui from '../components/ui.module.css'
import Toasts from '../indexerTools/Toast'
import Icon from '../components/Icon'
import Loader from '../components/Loader'
import { Empty } from './Empty'
import { useIsAdmin, useLiveProgress, useOngoing } from '../status/useOngoing'
import DownloadCard, { type DownloadActions } from './DownloadCard'
import { RemoveDialog } from './Dialogs'
import { RemoveFromClientDialog } from '../downloadClients/TorrentActivity'
import { isTorrent } from '../downloadClients/torrentFormat'
import { downloadTypeOptions, matchesSearch, matchesType, pick, useCustomCategories } from './filterOptions'
import { FilterBar, FilterSelect, NoMatches, SearchBox } from './Filters'
import { useDownloadAction, useMediaLinks, useToasts } from './hooks'
import { PendingImports } from './imports'
import { useClampPage, useListParams } from './listParams'
import Pager from './Pager'
import { usePageSize } from './paging'
import styles from './Activity.module.css'

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

/** Active downloads with live progress, the filters and the per-download actions (queue totals live in the page head). */
export default function QueueTab({ onAddNzb }: { onAddNzb: () => void }) {
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
    ({ id, deleteFiles, fromClient }: { id: number; deleteFiles: boolean; fromClient?: boolean }) => downloadsApi.remove(id, deleteFiles, fromClient),
    fail('Could not remove'),
    ({ fromClient }) => {
      setRemoving(null)
      push('ok', fromClient ? 'Removed from the client' : 'Download removed')
    },
  )

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
  // Without a Usenet server only Usenet downloads are stuck; torrents run in their client.
  const noServer = (ongoing.data?.notices.some((n) => n.key === 'no-usenet-server') ?? false) && !(queue.data?.items.length && queue.data.items.every(isTorrent))
  const data = queue.data

  let body: ReactNode
  if (blackhole) {
    body = (
      <Empty icon="folder" title="Downloads go to a watched folder">
        <p>
          StreamZH hands every grabbed NZB to the folder <span className={ui.mono}>{settings.data?.nzbFolder}</span>. SABnzbd, NZBGet or
          another downloader picks it up from there, so nothing shows up in this queue.
        </p>
        <div className={styles.emptyActions}>
          <Link className={`${ui.button} ${ui.sm}`} to="/settings/media-management">
            Settings → Downloads
          </Link>
          <Link className={`${ui.button} ${ui.sm}`} to="/activity?tab=history">
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
          <button type="button" className={`${ui.button} ${ui.sm}`} onClick={() => void queue.refetch()}>
            Try again
          </button>
        </div>
      </Empty>
    )
  } else if (!data) {
    body = <Loader text="Loading the queue" />
  } else if (items.length === 0 && !active) {
    body = noServer ? (
      <Empty icon="server" title="Add your Usenet server first">
        <p>StreamZH downloads releases itself. It needs the address and login of your Usenet provider before anything can start.</p>
        <div className={styles.emptyActions}>
          <Link className={`${ui.primary} ${ui.sm}`} to="/settings/usenet">
            Settings → Usenet servers
          </Link>
        </div>
      </Empty>
    ) : (
      <Empty icon="download" title="Nothing is downloading">
        <p>Grabbed releases appear here with their progress, from download to repair and unpack. Search a movie or episode, or add an NZB file yourself.</p>
        <div className={styles.emptyActions}>
          <Link className={`${ui.button} ${ui.sm}`} to="/library/movies">
            <Icon name="film" size={14} /> Movies
          </Link>
          <Link className={`${ui.button} ${ui.sm}`} to="/library/series">
            <Icon name="tv" size={14} /> Series
          </Link>
          {admin && (
            <button type="button" className={`${ui.button} ${ui.sm}`} onClick={onAddNzb}>
              <Icon name="upload" size={14} /> Add NZB
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
            <Icon name="alert" size={15} />
            <span>
              No Usenet server is configured, so these downloads cannot start. <Link to="/settings/usenet">Add a server</Link>
            </span>
          </p>
        )}
        <FilterBar label="Filter the queue" active={active} matches={filtered.length + pendingMatches} noun="downloads" onClear={clear}>
          <FilterSelect label="Status" value={status} onChange={(v) => setFilters({ status: v })} options={statusOptions} />
          <FilterSelect label="Type" value={type} onChange={(v) => setFilters({ type: v })} options={downloadTypeOptions} groups={[{ label: 'Categories', options: categories }]} />
          <SearchBox value={search} onChange={setSearch} placeholder="Search titles" label="Search the queue" />
        </FilterBar>
        {active && filtered.length + pendingMatches === 0 && <NoMatches noun="downloads" onClear={clear} />}
        {pageItems.length > 0 && (
          <div className={styles.list}>
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
      {body}
      {!blackhole && (status === '' || status === 'import') && <PendingImports admin={admin} onToast={push} match={active ? matches : undefined} />}
      {removing && isTorrent(removing) && (
        <RemoveFromClientDialog
          item={removing}
          busy={remove.isPending}
          onClose={() => setRemoving(null)}
          onConfirm={(deleteFiles) => remove.mutate({ id: removing.id, deleteFiles, fromClient: true })}
        />
      )}
      {removing && !isTorrent(removing) && (
        <RemoveDialog
          item={removing}
          busy={remove.isPending}
          onClose={() => setRemoving(null)}
          onConfirm={(deleteFiles) => remove.mutate({ id: removing.id, deleteFiles })}
        />
      )}
      <Toasts toasts={toasts} onDismiss={dismiss} />
    </>
  )
}

/** Kept here for the Wanted page, which imports it from this module. */
export { Empty } from './Empty'
