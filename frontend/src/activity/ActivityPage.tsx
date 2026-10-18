import { useQuery } from '@tanstack/react-query'
import { useRef, useState, type KeyboardEvent } from 'react'
import { useSearchParams } from 'react-router'
import { ApiError } from '../api/client'
import { downloadKeys, downloadsApi, withLive } from '../api/downloads'
import { mediaManagementApi, mediaManagementKeys } from '../api/mediaManagement'
import Icon from '../components/Icon'
import PageHeader from '../components/PageHeader'
import ui from '../components/ui.module.css'
import Toasts from '../indexerTools/Toast'
import { formatBytes } from '../lib/format'
import { formatSpeed } from '../status/format'
import { useIsAdmin, useLiveProgress } from '../status/useOngoing'
import BlocklistTab from './BlocklistTab'
import { UploadDialog } from './Dialogs'
import { formatLeft } from './format'
import HistoryTab from './HistoryTab'
import { useDownloadAction, useToasts } from './hooks'
import QueueTab from './QueueTab'
import styles from './Activity.module.css'

const tabs = [
  { key: 'queue', label: 'Queue', icon: 'queue' },
  { key: 'history', label: 'History', icon: 'history' },
  { key: 'blocklist', label: 'Blocklist', icon: 'ban' },
] as const

type TabKey = (typeof tabs)[number]['key']

const lowSpace = 10 * 1024 ** 3

/** Downloads in progress and waiting for import (Queue), what finished, failed or was imported (History), and the Blocklist. */
export default function ActivityPage() {
  const [url, setUrl] = useSearchParams()
  const requested = url.get('tab')
  const tab: TabKey = tabs.find((t) => t.key === requested)?.key ?? 'queue'
  const admin = useIsAdmin()
  const queue = useQuery({
    queryKey: downloadKeys.queue,
    queryFn: downloadsApi.queue,
    refetchInterval: (q) => (q.state.data?.items.length ? 10_000 : 30_000),
    retry: (count, error) => !(error instanceof ApiError && error.status === 404) && count < 1,
  })
  const settings = useQuery({ queryKey: downloadKeys.settings, queryFn: downloadsApi.settings, enabled: admin, retry: false })
  const pending = useQuery({ queryKey: mediaManagementKeys.pending, queryFn: mediaManagementApi.pending, enabled: false })
  const live = useLiveProgress()
  const { toasts, push, dismiss } = useToasts()
  const [uploading, setUploading] = useState(false)
  const refs = useRef<Record<TabKey, HTMLButtonElement | null>>({ queue: null, history: null, blocklist: null })

  const pauseAll = useDownloadAction(downloadsApi.pauseAll, (m) => push('error', 'Could not pause the queue', m))
  const resumeAll = useDownloadAction(downloadsApi.resumeAll, (m) => push('error', 'Could not resume the queue', m))

  const select = (key: TabKey) => setUrl(key === 'queue' ? {} : { tab: key })

  // Arrow keys move between the tabs (WAI-ARIA tabs pattern).
  const onKeyDown = (e: KeyboardEvent) => {
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight' && e.key !== 'Home' && e.key !== 'End') return
    e.preventDefault()
    const index = tabs.findIndex((t) => t.key === tab)
    const next =
      e.key === 'Home' ? 0 : e.key === 'End' ? tabs.length - 1 : (index + (e.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length
    select(tabs[next].key)
    refs.current[tabs[next].key]?.focus()
  }

  // The line under the title: queue size, overall speed, time left and free space.
  const data = queue.data
  const blackhole = settings.data?.client === 'blackhole'
  const items = (data?.items ?? []).map((i) => withLive(i, live, queue.dataUpdatedAt))
  const liveNewer = live && live.at > queue.dataUpdatedAt
  const speed = liveNewer ? Object.values(live.items).reduce((sum, i) => sum + (i.speedBps || 0), 0) : (data?.speedBps ?? 0)
  const remaining = items.reduce((sum, i) => sum + Math.max(0, i.totalBytes - i.downloadedBytes), 0)
  const eta = speed > 0 && remaining > 0 ? remaining / speed : null
  const lowFree = data?.freeSpaceBytes != null && data.freeSpaceBytes < lowSpace
  const count = items.length + (pending.data?.length ?? 0)

  const lead = blackhole ? (
    'Grabbed releases go to a watched folder for an external downloader.'
  ) : data ? (
    <span className={styles.leadLine}>
      <span>
        <b className={ui.num}>{items.length}</b> {items.length === 1 ? 'download' : 'downloads'}
        {remaining > 0 && <> · {formatBytes(remaining)} left</>}
      </span>
      <span className={styles.leadSep} aria-hidden="true" />
      <span>
        <Icon name="gauge" size={14} />
        {data.paused ? 'Paused' : <b className={ui.num}>{formatSpeed(speed)}</b>}
        {eta != null && !data.paused && <span className={ui.num}> · {formatLeft(eta)}</span>}
        {data.speedLimitKBps > 0 && <span className={ui.muted}> · limit {formatSpeed(data.speedLimitKBps * 1024)}</span>}
      </span>
      {data.freeSpaceBytes != null && (
        <>
          <span className={styles.leadSep} aria-hidden="true" />
          <span className={lowFree ? styles.leadWarn : undefined} title={lowFree ? 'Low free space: downloads may stop' : 'Free space in the download folder'}>
            <Icon name="hdd" size={14} />
            <b className={ui.num}>{formatBytes(data.freeSpaceBytes)}</b> free
          </span>
        </>
      )}
    </span>
  ) : (
    'Downloads in progress, from grab to import, and everything that finished, failed or was blocklisted.'
  )

  const actions =
    admin && !blackhole ? (
      <>
        {data?.paused ? (
          <button type="button" className={ui.primary} onClick={() => resumeAll.mutate()} disabled={resumeAll.isPending}>
            <Icon name="play" size={15} /> Resume all
          </button>
        ) : (
          <button type="button" className={ui.button} onClick={() => pauseAll.mutate()} disabled={!data || pauseAll.isPending || items.length === 0}>
            <Icon name="pause" size={15} /> Pause all
          </button>
        )}
        <button type="button" className={ui.button} onClick={() => setUploading(true)}>
          <Icon name="upload" size={15} /> Add NZB
        </button>
      </>
    ) : undefined

  return (
    <div className={styles.activity}>
      <PageHeader title="Activity" lead={lead} actions={actions} />

      <div className={styles.tabs} role="tablist" aria-label="Activity" onKeyDown={onKeyDown}>
        {tabs.map((t) => (
          <button
            key={t.key}
            ref={(el) => {
              refs.current[t.key] = el
            }}
            type="button"
            role="tab"
            id={`activity-tab-${t.key}`}
            aria-selected={tab === t.key}
            aria-controls={`activity-panel-${t.key}`}
            tabIndex={tab === t.key ? 0 : -1}
            className={styles.tab}
            onClick={() => select(t.key)}
          >
            <Icon name={t.icon} size={15} />
            {t.label}
            {t.key === 'queue' && count > 0 && <span className={styles.tcount}>{count}</span>}
          </button>
        ))}
      </div>

      <section id={`activity-panel-${tab}`} role="tabpanel" aria-labelledby={`activity-tab-${tab}`} className={styles.tabPanel}>
        {tab === 'queue' ? <QueueTab onAddNzb={() => setUploading(true)} /> : tab === 'history' ? <HistoryTab /> : <BlocklistTab />}
      </section>

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
    </div>
  )
}
