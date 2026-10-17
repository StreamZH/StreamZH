import { useQuery } from '@tanstack/react-query'
import { useMemo } from 'react'
import { downloadKeys, type DownloadProgressItem, type LiveProgress } from '../api/downloads'
import { libraryApi, queryKeys, type JobStatus } from '../api/library'
import { statusApi, statusKeys, type Ongoing, type OngoingRunning } from '../api/status'
import { useAuth } from '../auth/auth'
import { formatEta, formatSpeed, joinParts, percent } from './format'

/** A running row with the time the browser first saw it, for the elapsed time of work without progress ("2:14"). */
export type SeenRunning = OngoingRunning & { seenAt?: number }

export interface SeenOngoing extends Ongoing {
  running: SeenRunning[]
}

const firstSeen = new Map<string, number>()

/** Fetches /status/ongoing and stamps every running row with the time it first appeared. */
async function fetchOngoing(): Promise<SeenOngoing> {
  const data = await statusApi.ongoing()
  const at = Date.now()
  const keys = new Set(data.running.map((r) => r.key))
  for (const key of firstSeen.keys()) if (!keys.has(key)) firstSeen.delete(key)
  return {
    ...data,
    running: data.running.map((row) => {
      if (!firstSeen.has(row.key)) firstSeen.set(row.key, at)
      return { ...row, seenAt: firstSeen.get(row.key) }
    }),
  }
}

/** Owner and Admin see configuration, the status window and the actions on the Activity page. */
export function useIsAdmin(): boolean {
  const { user } = useAuth()
  return user?.role === 'Owner' || user?.role === 'Admin'
}

/** The newest "downloadProgress" hub message (never fetched; useLiveConnection fills it). */
export function useLiveProgress(): LiveProgress | undefined {
  return useQuery({
    queryKey: downloadKeys.live,
    queryFn: (): LiveProgress => ({ at: 0, items: {} }),
    enabled: false,
    staleTime: Infinity,
  }).data
}

/**
 * GET /status/ongoing for the status window, with live data applied between refetches: download progress from the hub
 * ("downloadProgress") and the job queue ("jobs"). Polls every 15 s while something runs, every minute otherwise, as a
 * safety net next to the hub messages.
 */
export function useOngoing() {
  const admin = useIsAdmin()
  const query = useQuery({
    queryKey: statusKeys.ongoing,
    queryFn: fetchOngoing,
    enabled: admin,
    refetchInterval: (q) => (q.state.data?.running.length ? 15_000 : 60_000),
  })
  const jobs = useQuery({ queryKey: queryKeys.jobs, queryFn: libraryApi.jobs, enabled: admin })
  const live = useLiveProgress()

  const data = useMemo(
    () => (query.data ? applyLive(query.data, query.dataUpdatedAt, live, jobs.data, jobs.dataUpdatedAt) : undefined),
    [query.data, query.dataUpdatedAt, live, jobs.data, jobs.dataUpdatedAt],
  )

  return { admin, data, isError: query.isError }
}

function applyLive(
  ongoing: SeenOngoing,
  fetchedAt: number,
  live: LiveProgress | undefined,
  jobs: JobStatus | undefined,
  jobsAt: number,
): SeenOngoing {
  let running: SeenRunning[] = ongoing.running

  if (live && live.at > fetchedAt) {
    running = running.flatMap((row) => {
      const id = row.key.startsWith('download-') ? Number(row.key.slice(9)) : NaN
      const update = Number.isNaN(id) ? undefined : live.items[id]
      if (!update) return [row]
      if (update.status === 'completed' || update.status === 'failed') return []
      return [liveRow(row, update)]
    })
  }

  if (jobs && jobsAt > fetchedAt) {
    const rest = running.filter((r) => r.key !== 'job')
    const previous = running.find((r) => r.key === 'job')
    const seenAt = previous && previous.title === jobs.running ? previous.seenAt : jobsAt
    const job: SeenRunning | null = jobs.running
      ? { ...jobRow(jobs.running, jobs.queued > 0 ? `${jobs.queued} more queued` : 'Running'), seenAt }
      : jobs.queued > 0
        ? { ...jobRow('Background jobs', `${jobs.queued} queued`), seenAt }
        : null
    if (job) {
      const at = running.findIndex((r) => r.key === 'job')
      const index = at >= 0 ? at : rest.filter((r) => r.kind === 'postprocess' || r.speedBps != null).length
      rest.splice(index, 0, job)
    }
    running = rest
  }

  return running === ongoing.running ? ongoing : { ...ongoing, running }
}

const jobRow = (title: string, sub: string): OngoingRunning => ({
  key: 'job',
  kind: 'job',
  title,
  sub,
  progress: null,
  speedBps: null,
  etaSeconds: null,
  link: null,
})

const stepNames: Partial<Record<DownloadProgressItem['status'], string>> = {
  verifying: 'Verifying',
  repairing: 'Repairing',
  extracting: 'Extracting',
}

function liveRow(row: SeenRunning, update: DownloadProgressItem): SeenRunning {
  switch (update.status) {
    case 'verifying':
    case 'repairing':
    case 'extracting':
      return {
        ...row,
        kind: 'postprocess',
        progress: update.stepProgress,
        speedBps: null,
        etaSeconds: null,
        sub: joinParts(update.statusMessage ?? stepNames[update.status], update.stepProgress != null && percent(update.stepProgress)),
      }
    case 'downloading':
      return {
        ...row,
        kind: 'download',
        progress: update.progress,
        speedBps: update.speedBps,
        etaSeconds: update.etaSeconds,
        sub: joinParts(
          percent(update.progress),
          update.speedBps > 0 ? formatSpeed(update.speedBps) : 'waiting for connections',
          update.speedBps > 0 && formatEta(update.etaSeconds),
        ),
      }
    case 'paused':
      return { ...row, kind: 'download', progress: update.progress, speedBps: null, etaSeconds: null, sub: `Paused · ${percent(update.progress)}` }
    default:
      return row
  }
}
