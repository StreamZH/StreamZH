import { request } from './client'

export type DownloadClient = 'builtin' | 'blackhole'
export type DuplicateAction = 'off' | 'tag' | 'pause' | 'fail'
export type QueueSort = 'off' | 'oldest' | 'newest' | 'smallest' | 'largest' | 'name'
export type UnwantedAction = 'off' | 'warn' | 'pause' | 'fail'
export type EncryptedAction = 'off' | 'pause' | 'fail'
export type ToolPriority = 'normal' | 'belowNormal' | 'idle'

/** Settings → Downloads (GET/PUT /settings/downloads). Optional folders are '' when off. */
export interface DownloadSettings {
  client: DownloadClient
  nzbFolder: string
  defaultNzbFolder: string
  isDefault: boolean
  problem: string | null
  incompleteFolder: string
  defaultIncompleteFolder: string
  incompleteProblem: string | null
  completeFolder: string
  defaultCompleteFolder: string
  completeProblem: string | null
  speedLimitKBps: number
  minFreeSpaceGb: number
  keepFiles: boolean
  abortHopeless: boolean
  articleTries: number
  nzbBackupFolder: string
  nzbBackupProblem: string | null
  watchFolder: string
  watchProblem: string | null
  watchIntervalSeconds: number
  pauseOnStart: boolean
  preCheck: boolean
  maxActiveDownloads: number
  propagationDelayMinutes: number
  duplicateAction: DuplicateAction
  autoSort: QueueSort
  unwantedExtensions: string
  unwantedAction: UnwantedAction
  encryptedAction: EncryptedAction
  downloadAllPar2: boolean
  onlyVerified: boolean
  recursiveUnpack: boolean
  ignoreSamples: boolean
  cleanupList: string
  deobfuscate: boolean
  temporaryFolderName: boolean
  par2Arguments: string
  sevenZipArguments: string
  toolPriority: ToolPriority
  postProcessWorkers: number
  maxLineSpeedKBps: number
  historyKeepDays: number
  historyKeepItems: number
  historyKeepFailed: boolean
}

/** The fields that can be changed; left out = unchanged. */
export type DownloadSettingsChange = Partial<
  Omit<
    DownloadSettings,
    | 'defaultNzbFolder'
    | 'isDefault'
    | 'problem'
    | 'defaultIncompleteFolder'
    | 'incompleteProblem'
    | 'defaultCompleteFolder'
    | 'completeProblem'
    | 'nzbBackupProblem'
    | 'watchProblem'
  >
>

export type CategoryPriority = 'default' | 'paused' | 'low' | 'normal' | 'high' | 'force'
export type PostProcessLevel = 'none' | 'repair' | 'unpack' | 'full'

/** A download category. `path` is where finished downloads go; `folder` as entered ('' = the name). */
export interface DownloadCategory {
  id: number
  name: string
  folder: string
  path: string
  priority: CategoryPriority
  postProcessing: PostProcessLevel
  isDefault: boolean
  /** Paused by the scheduler. */
  paused: boolean
}

export interface DownloadCategoryInput {
  name: string
  folder: string
  priority: CategoryPriority
  postProcessing: PostProcessLevel
}

/** For pickers (Add NZB, Search page). */
export interface DownloadCategoryChoice {
  name: string
  isDefault: boolean
  kind: 'movie' | 'series' | 'other'
}

export type ScheduleAction = 'pause' | 'resume' | 'speedLimit' | 'pauseServer' | 'resumeServer' | 'pauseCategory' | 'resumeCategory'
export type Day = 'mon' | 'tue' | 'wed' | 'thu' | 'fri' | 'sat' | 'sun'

export const days: { value: Day; label: string }[] = [
  { value: 'mon', label: 'Mon' },
  { value: 'tue', label: 'Tue' },
  { value: 'wed', label: 'Wed' },
  { value: 'thu', label: 'Thu' },
  { value: 'fri', label: 'Fri' },
  { value: 'sat', label: 'Sat' },
  { value: 'sun', label: 'Sun' },
]

/** A scheduler rule: at `time` (HH:mm, server time) on `days` do `action`. */
export interface DownloadSchedule {
  id: number
  enabled: boolean
  time: string
  days: Day[]
  action: ScheduleAction
  /** speedLimit: KB/s or "50%"; servers: the id; categories: the name. */
  argument: string
  description: string
}

export type DownloadScheduleInput = Omit<DownloadSchedule, 'id' | 'description'>

export const downloadSettingsKeys = {
  settings: ['settings-downloads'] as const,
  categories: ['settings-downloads', 'categories'] as const,
  schedules: ['settings-downloads', 'schedules'] as const,
  choices: ['download-categories'] as const,
}

export const downloadSettingsApi = {
  get: () => request<DownloadSettings>('GET', '/settings/downloads'),
  save: (change: DownloadSettingsChange) => request<DownloadSettings>('PUT', '/settings/downloads', change),
  categories: () => request<DownloadCategory[]>('GET', '/settings/downloads/categories'),
  addCategory: (category: DownloadCategoryInput) => request<DownloadCategory>('POST', '/settings/downloads/categories', category),
  saveCategory: (id: number, category: DownloadCategoryInput) => request<DownloadCategory>('PUT', `/settings/downloads/categories/${id}`, category),
  deleteCategory: (id: number) => request<void>('DELETE', `/settings/downloads/categories/${id}`),
  schedules: () => request<DownloadSchedule[]>('GET', '/settings/downloads/schedules'),
  addSchedule: (rule: DownloadScheduleInput) => request<DownloadSchedule>('POST', '/settings/downloads/schedules', rule),
  saveSchedule: (id: number, rule: DownloadScheduleInput) => request<DownloadSchedule>('PUT', `/settings/downloads/schedules/${id}`, rule),
  deleteSchedule: (id: number) => request<void>('DELETE', `/settings/downloads/schedules/${id}`),
  choices: () => request<DownloadCategoryChoice[]>('GET', '/download/categories'),
}
