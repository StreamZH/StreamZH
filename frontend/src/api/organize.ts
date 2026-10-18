import { request } from './client'
import type { MediaType } from './library'

export type OrganizeFileKind = 'video' | 'subtitle' | 'metadata' | 'extra'
export type LibraryRunKind = 'organize' | 'undo' | 'move'

export interface OrganizeFileChange {
  existingPath: string
  newPath: string
  kind: OrganizeFileKind
}

export interface OrganizeItem {
  mediaType: MediaType
  id: number
  title: string
  year: number
  existingFolder: string
  newFolder: string
  folderChanged: boolean
  caseOnly: boolean
  folderExists: boolean
  files: OrganizeFileChange[]
  conflicts: string[]
  notes: string[]
  otherFileCount: number
  otherFiles: string[]
  hasChanges: boolean
  canApply: boolean
}

export interface OrganizeError {
  id: number
  title: string
  message: string
}

export interface LibraryRunResult {
  kind: LibraryRunKind
  mediaType: MediaType
  runId: number | null
  finished: string
  done: number
  unchanged: number
  skipped: number
  failed: number
  errors: OrganizeError[]
}

export interface LibraryRunProgress {
  kind: LibraryRunKind
  mediaType: MediaType
  title: string
  done: number
  total: number
  current: string | null
  progress: number
  started: boolean
}

export interface OrganizeStatus {
  running: LibraryRunProgress | null
  last: LibraryRunResult | null
  undo: { runId: number; mediaType: MediaType; items: number; finished: string; until: string } | null
  /** Where deleted files go; null: deleted for good. */
  recycleBin: string | null
}

export interface MassEditChanges {
  monitored?: boolean
  qualityProfileId?: number
  rootFolderId?: number
  moveFiles?: boolean
}

export interface DeleteResult {
  removed: { id: number; title: string; folder: string; filesDeleted: boolean; recycledTo: string | null }[]
  errors: OrganizeError[]
}

const editor = (type: MediaType) => (type === 'Movie' ? '/movie/editor' : '/series/editor')
const idsField = (type: MediaType) => (type === 'Movie' ? 'movieIds' : 'seriesIds')

export const organizeKeys = {
  status: ['organize-status'] as const,
  preview: (type: MediaType, ids: number[]) => ['organize-preview', type, ids] as const,
}

export const organizeApi = {
  preview: (mediaType: MediaType, ids: number[]) => request<OrganizeItem[]>('POST', '/organize/preview', { mediaType, ids }),
  apply: (mediaType: MediaType, ids: number[]) => request<{ queued: boolean }>('POST', '/organize/apply', { mediaType, ids }),
  undo: (runId: number) => request<{ queued: boolean }>('POST', '/organize/undo', { runId }),
  status: () => request<OrganizeStatus>('GET', '/organize/status'),
  edit: (type: MediaType, ids: number[], changes: MassEditChanges) =>
    request<{ updated: number; moving: number }>('PUT', editor(type), { [idsField(type)]: ids, ...changes }),
  remove: (type: MediaType, ids: number[], deleteFiles: boolean) =>
    request<DeleteResult>('DELETE', editor(type), { [idsField(type)]: ids, deleteFiles }),
}
