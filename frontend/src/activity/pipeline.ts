import type { DownloadItem } from '../api/downloads'

/** The stages of one download, as in the StreamZH concept: Search → Grab → Download → Verify & Unpack → Import → Notify. */
export const stages = [
  { key: 'grab', label: 'Grab' },
  { key: 'download', label: 'Download' },
  { key: 'verify', label: 'Verify' },
  { key: 'repair', label: 'Repair' },
  { key: 'extract', label: 'Extract' },
  { key: 'done', label: 'Done' },
] as const

/** Import & rename and notifications arrive in 0.6. */
export const later = [
  { key: 'import', label: 'Import' },
  { key: 'notify', label: 'Notify' },
] as const

export type StageKey = (typeof stages)[number]['key']
export type StageState = 'done' | 'current' | 'waiting' | 'paused' | 'failed' | 'todo'

const stageOf: Record<DownloadItem['status'], StageKey> = {
  queued: 'download',
  downloading: 'download',
  paused: 'download',
  verifying: 'verify',
  repairing: 'repair',
  extracting: 'extract',
  completed: 'done',
  failed: 'download',
}

/** Where a failed download stopped: unfinished downloads in Download, otherwise from the error text. */
export function failedStage(item: Pick<DownloadItem, 'progress' | 'error' | 'statusMessage'>): StageKey {
  if (item.progress < 0.999) return 'download'
  const text = `${item.error ?? ''} ${item.statusMessage ?? ''}`.toLowerCase()
  if (/extract|unrar|unpack|archive|password|7z|zip/.test(text)) return 'extract'
  if (/repair|par2|blocks/.test(text)) return 'repair'
  if (/verif/.test(text)) return 'verify'
  return 'download'
}

export function stageStates(item: Pick<DownloadItem, 'status' | 'progress' | 'error' | 'statusMessage'>): Record<StageKey, StageState> {
  const current = item.status === 'failed' ? failedStage(item) : stageOf[item.status]
  const index = stages.findIndex((s) => s.key === current)
  const states = {} as Record<StageKey, StageState>
  stages.forEach((stage, i) => {
    if (item.status === 'completed') states[stage.key] = 'done'
    else if (i < index) states[stage.key] = 'done'
    else if (i > index) states[stage.key] = 'todo'
    else
      states[stage.key] =
        item.status === 'failed' ? 'failed' : item.status === 'paused' ? 'paused' : item.status === 'queued' ? 'waiting' : 'current'
  })
  return states
}

export const stateText: Record<StageState, string> = {
  done: 'done',
  current: 'in progress',
  waiting: 'waiting',
  paused: 'paused',
  failed: 'failed',
  todo: 'not started',
}
