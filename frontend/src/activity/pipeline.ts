import type { DownloadItem } from '../api/downloads'
import type { ImportStateName } from '../api/mediaManagement'

/** The stages of one download, as in the StreamZH concept: Search → Grab → Download → Verify & Unpack → Import → Notify. */
export const stages = [
  { key: 'grab', label: 'Grab' },
  { key: 'download', label: 'Download' },
  { key: 'verify', label: 'Verify' },
  { key: 'repair', label: 'Repair' },
  { key: 'extract', label: 'Extract' },
  { key: 'import', label: 'Import' },
] as const

/** Notifications (media servers, Discord …) are not part of this strip yet. */

export type StageKey = (typeof stages)[number]['key']
export type StageState = 'done' | 'current' | 'waiting' | 'paused' | 'failed' | 'todo'

/** What the strip needs to know: the download, and the state of its import once it is completed. */
export type PipelineItem = Pick<DownloadItem, 'status' | 'progress' | 'error' | 'statusMessage' | 'mediaType'> & {
  importState?: ImportStateName | null
}

const stageOf: Record<DownloadItem['status'], StageKey> = {
  queued: 'download',
  downloading: 'download',
  paused: 'download',
  verifying: 'verify',
  repairing: 'repair',
  extracting: 'extract',
  completed: 'import',
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

/** State of the Import stage of a completed download. */
function importStage(item: PipelineItem): StageState {
  switch (item.importState) {
    case 'imported':
      return 'done'
    case 'importing':
      return 'current'
    case 'failed':
      return 'failed'
    case 'queued':
    case 'waiting':
      return 'waiting'
    default:
      // Linked downloads are imported by completed download handling; the rest waits for Import….
      return item.mediaType ? 'waiting' : 'todo'
  }
}

export function stageStates(item: PipelineItem): Record<StageKey, StageState> {
  const current = item.status === 'failed' ? failedStage(item) : stageOf[item.status]
  const index = stages.findIndex((s) => s.key === current)
  const states = {} as Record<StageKey, StageState>
  stages.forEach((stage, i) => {
    if (item.status === 'completed') states[stage.key] = stage.key === 'import' ? importStage(item) : 'done'
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
