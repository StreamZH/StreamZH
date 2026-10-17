import { request } from './client'

export type OngoingKind = 'job' | 'download' | 'postprocess'
export type NoticeLevel = 'info' | 'warn' | 'error'

/** Something running right now (GET /status/ongoing). Download rows have the key "download-{id}". */
export interface OngoingRunning {
  key: string
  kind: OngoingKind
  title: string
  sub: string | null
  /** 0..1, null when there is no measurable progress. */
  progress: number | null
  speedBps: number | null
  etaSeconds: number | null
  link: string | null
}

/** Something worth a look ("Heads-up"). Dismissible ones can be hidden per browser. */
export interface OngoingNotice {
  key: string
  level: NoticeLevel
  title: string
  sub: string | null
  link: string | null
  dismissible: boolean
}

export interface Ongoing {
  running: OngoingRunning[]
  notices: OngoingNotice[]
}

export const statusKeys = {
  ongoing: ['status', 'ongoing'] as const,
}

export const statusApi = {
  ongoing: () => request<Ongoing>('GET', '/status/ongoing'),
}
