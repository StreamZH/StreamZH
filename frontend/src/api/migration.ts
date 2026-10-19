import { request } from './client'

// The migration wizard (0.9): take over settings and libraries from Radarr, Sonarr, Prowlarr and SABnzbd
// (/api/v1/migration, admins only), and the side-by-side switch (/api/v1/settings/automation).

export type MigrationSource = 'radarr' | 'sonarr' | 'prowlarr' | 'sabnzbd'
export type MigrationAction = 'create' | 'update' | 'link' | 'skip'
export type MigrationMessageLevel = 'info' | 'warn' | 'error'
export type MigrationRunStatus = 'queued' | 'running' | 'done' | 'failed' | 'cancelled'

export interface MigrationSectionInfo {
  key: string
  label: string
  description: string
  order: number
  defaultSelected: boolean
  advanced: boolean
}

/** GET /migration/sources: every source with the parts it can take over. */
export interface MigrationSourceInfo {
  key: MigrationSource
  label: string
  sections: MigrationSectionInfo[]
}

export interface PathMapping {
  remote: string
  local: string
}

export interface MigrationRequest {
  url: string
  apiKey: string
  pathMappings?: PathMapping[]
  /** Null/missing: the default selection. */
  sections?: string[]
  exclude?: string[]
  secrets?: Record<string, string>
  options?: Record<string, string>
  dryRun?: boolean
}

export interface MigrationAppInfo {
  appName: string
  version: string
  instanceName?: string | null
}

export interface MigrationTestResult {
  ok: boolean
  app: MigrationAppInfo | null
  error: string | null
}

export interface MigrationItem {
  key: string
  label: string
  action: MigrationAction
  detail?: string | null
  reason?: string | null
  warning?: string | null
  needsSecret?: boolean
}

export interface MigrationCounts {
  create: number
  update: number
  link: number
  skip: number
}

export interface MigrationSectionPreview {
  key: string
  label: string
  description: string
  order: number
  defaultSelected: boolean
  advanced: boolean
  items: MigrationItem[]
  warnings: string[]
  counts: MigrationCounts
}

export interface MigrationPathCheck {
  remote: string
  local: string
  exists: boolean
  mapped: boolean
  /** Outside the storage locations: StreamZH did not look at it (the owner allows it in the Folders step). */
  outsideStorage?: boolean
}

export interface MigrationPreview {
  source: MigrationSource
  app: MigrationAppInfo
  sections: MigrationSectionPreview[]
  paths: MigrationPathCheck[]
  warnings: string[]
}

export interface MigrationMessage {
  level: MigrationMessageLevel
  text: string
  item?: string | null
}

export interface MigrationSectionReport {
  key: string
  label: string
  created: number
  updated: number
  linked: number
  skipped: number
  failed: number
  messages: MigrationMessage[]
}

export interface MigrationRun {
  id: number
  source: MigrationSource
  status: MigrationRunStatus
  dryRun: boolean
  started: string
  finished: string | null
  /** 0..1 */
  progress: number
  currentStep: string | null
  error: string | null
  app: MigrationAppInfo | null
  report: { sections: MigrationSectionReport[] }
}

/** GET/PUT /settings/automation: while `hold` is on, StreamZH grabs nothing by itself ("side by side"). */
export interface AutomationSettings {
  hold: boolean
  since: string | null
}

export const migrationKeys = {
  sources: ['migration', 'sources'] as const,
  runs: ['migration', 'runs'] as const,
  run: (id: number) => ['migration', 'runs', id] as const,
  automation: ['settings', 'automation'] as const,
}

export const migrationApi = {
  sources: () => request<MigrationSourceInfo[]>('GET', '/migration/sources'),
  test: (source: MigrationSource, body: MigrationRequest) => request<MigrationTestResult>('POST', `/migration/${source}/test`, body),
  preview: (source: MigrationSource, body: MigrationRequest) => request<MigrationPreview>('POST', `/migration/${source}/preview`, body),
  run: (source: MigrationSource, body: MigrationRequest) => request<MigrationRun>('POST', `/migration/${source}/run`, body),
  runs: () => request<MigrationRun[]>('GET', '/migration/runs'),
  find: (id: number) => request<MigrationRun>('GET', `/migration/runs/${id}`),
  cancel: (id: number) => request<void>('POST', `/migration/runs/${id}/cancel`),
  automation: () => request<AutomationSettings>('GET', '/settings/automation'),
  setAutomation: (hold: boolean) => request<AutomationSettings>('PUT', '/settings/automation', { hold }),
}

export const isLive = (run: Pick<MigrationRun, 'status'>) => run.status === 'queued' || run.status === 'running'
