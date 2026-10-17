import { request } from './client'

/** par2 (repair) and 7-Zip (extraction), called as external programs by post-processing. */
export interface ToolSettings {
  /** Configured path; '' = search automatically. */
  par2Path: string
  sevenZipPath: string
  par2Found: boolean
  sevenZipFound: boolean
  par2Version: string | null
  sevenZipVersion: string | null
  /** The executable that is used, wherever it was found. */
  par2ResolvedPath: string | null
  sevenZipResolvedPath: string | null
  par2Source: ToolSource
  sevenZipSource: ToolSource
  /** Set when the configured path does not point to the tool. */
  par2Problem: string | null
  sevenZipProblem: string | null
  bundledFolder: string
  os: 'Windows' | 'Linux' | 'macOS'
}

export type ToolSource = 'NotFound' | 'Configured' | 'Bundled' | 'Path' | 'WindowsDefault'

export const toolKeys = {
  settings: ['settings-tools'] as const,
}

export const toolsApi = {
  settings: () => request<ToolSettings>('GET', '/settings/tools'),
  /** '' switches back to the automatic search. */
  save: (paths: { par2Path: string; sevenZipPath: string }) => request<ToolSettings>('PUT', '/settings/tools', paths),
}
