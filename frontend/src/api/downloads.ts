import { ApiError, request, unauthorizedEvent } from './client'
import type { MediaType } from './library'

export type DownloadStatus =
  | 'queued'
  | 'downloading'
  | 'paused'
  | 'verifying'
  | 'repairing'
  | 'extracting'
  | 'completed'
  | 'failed'

export type DownloadCategory = 'movie' | 'series' | 'other'

export interface DownloadItem {
  id: number
  title: string
  category: DownloadCategory
  status: DownloadStatus
  priority: number
  totalBytes: number
  downloadedBytes: number
  /** 0..1 of the download itself. */
  progress: number
  speedBps: number
  etaSeconds: number | null
  /** 0..1 of the current post-processing step (verify, repair, extract). */
  stepProgress: number | null
  statusMessage: string | null
  missingArticles: number
  error: string | null
  indexer: string | null
  mediaType: MediaType | null
  movieId: number | null
  seriesId: number | null
  episodeIds: number[]
  outputPath: string | null
  added: string
  completed: string | null
}

export interface DownloadFile {
  name: string
  size: number
  downloaded: number
  kind: string
  missingArticles: number
}

export interface DownloadDetail extends DownloadItem {
  files: DownloadFile[]
}

/** GET /download: the active queue (everything not completed or failed). */
export interface DownloadQueue {
  items: DownloadItem[]
  speedBps: number
  paused: boolean
  freeSpaceBytes: number | null
  /** 0 = unlimited. */
  speedLimitKBps: number
}

export interface DownloadHistoryPage {
  page: number
  pageSize: number
  totalRecords: number
  records: DownloadItem[]
}

/** Hub message "downloadProgress", about once per second while something is active. */
export interface DownloadProgressItem {
  id: number
  status: DownloadStatus
  progress: number
  speedBps: number
  etaSeconds: number | null
  stepProgress: number | null
  statusMessage: string | null
}

/** The latest "downloadProgress" message, kept in the query cache under downloadKeys.live. */
export interface LiveProgress {
  at: number
  items: Record<number, DownloadProgressItem>
}

export interface DownloadSettings {
  client?: 'builtin' | 'blackhole'
  nzbFolder: string
  incompleteFolder?: string
  completeFolder?: string
  speedLimitKBps?: number
}

/** SABnzbd-style priorities: higher runs first. */
export const downloadPriorities = [
  { value: -1, label: 'Low' },
  { value: 0, label: 'Normal' },
  { value: 1, label: 'High' },
  { value: 2, label: 'Force' },
] as const

export const activeStatuses: DownloadStatus[] = ['queued', 'downloading', 'paused', 'verifying', 'repairing', 'extracting']

export const downloadKeys = {
  all: ['downloads'] as const,
  queue: ['downloads', 'queue'] as const,
  history: (page: number, pageSize: number) => ['downloads', 'history', page, pageSize] as const,
  /** Not fetched: filled by the hub's "downloadProgress". Outside "downloads" so invalidating the queue keeps it. */
  live: ['download-progress'] as const,
  settings: ['download-settings'] as const,
}

/** multipart/form-data upload; the shared request helper only sends JSON. `category`: movie, series, other or a category name. */
async function upload(file: File, title?: string, category?: string): Promise<DownloadItem> {
  const form = new FormData()
  form.set('file', file)
  if (title) form.set('title', title)
  if (category) form.set('category', category)
  const response = await fetch('/api/v1/download', { method: 'POST', credentials: 'same-origin', body: form })
  if (response.status === 401) window.dispatchEvent(new Event(unauthorizedEvent))
  if (!response.ok) {
    let problem: { title?: string; detail?: string; errors?: Record<string, string[]> } = {}
    try {
      problem = await response.json()
    } catch {
      // no JSON body
    }
    throw new ApiError(response.status, problem.detail ?? problem.title ?? `${response.status} ${response.statusText}`, problem.errors ?? {})
  }
  return (await response.json().catch(() => undefined)) as DownloadItem
}

export const downloadsApi = {
  queue: () => request<DownloadQueue>('GET', '/download'),
  history: (page: number, pageSize: number) =>
    request<DownloadHistoryPage>('GET', `/download/history?page=${page}&pageSize=${pageSize}`),
  detail: (id: number) => request<DownloadDetail>('GET', `/download/${id}`),
  upload,
  pause: (id: number) => request<void>('POST', `/download/${id}/pause`),
  resume: (id: number) => request<void>('POST', `/download/${id}/resume`),
  retry: (id: number) => request<void>('POST', `/download/${id}/retry`),
  update: (id: number, changes: { priority?: number; category?: DownloadCategory }) =>
    request<DownloadItem>('PUT', `/download/${id}`, changes),
  remove: (id: number, deleteFiles: boolean) => request<void>('DELETE', `/download/${id}?deleteFiles=${deleteFiles}`),
  pauseAll: () => request<void>('POST', '/download/pause-all'),
  resumeAll: () => request<void>('POST', '/download/resume-all'),
  settings: () => request<DownloadSettings>('GET', '/settings/downloads'),
}

/** The queue item with the newest live progress applied, when the hub message is newer than the fetched data. */
export function withLive(item: DownloadItem, live: LiveProgress | undefined, fetchedAt: number): DownloadItem {
  const update = live && live.at > fetchedAt ? live.items[item.id] : undefined
  if (!update) return item
  return {
    ...item,
    status: update.status,
    progress: update.progress,
    downloadedBytes: Math.round(update.progress * item.totalBytes),
    speedBps: update.speedBps,
    etaSeconds: update.etaSeconds,
    stepProgress: update.stepProgress,
    statusMessage: update.statusMessage,
  }
}
