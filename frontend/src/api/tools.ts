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
  /** StreamZH can install the tool on this system. */
  par2Installable: boolean
  sevenZipInstallable: boolean
  /** An installation is running right now. */
  par2Installing: boolean
  sevenZipInstalling: boolean
  /** 0..1 while installing. */
  par2InstallProgress: number | null
  sevenZipInstallProgress: number | null
  /** Why the last installation failed. */
  par2InstallError: string | null
  sevenZipInstallError: string | null
  /** Version of the copy installed by StreamZH. */
  par2InstalledVersion: string | null
  sevenZipInstalledVersion: string | null
  /** The version StreamZH installs. */
  par2InstallVersion: string | null
  sevenZipInstallVersion: string | null
  /** Missing tools are installed automatically. */
  autoInstall: boolean
  /** Where StreamZH installs the tools (<data>/tools). */
  installFolder: string | null
}

export type ToolSource = 'NotFound' | 'Configured' | 'Installed' | 'Bundled' | 'Path' | 'WindowsDefault'

export type ToolName = 'par2' | 'sevenZip'

export const toolKeys = {
  settings: ['settings-tools'] as const,
}

export const toolsApi = {
  settings: () => request<ToolSettings>('GET', '/settings/tools'),
  /** '' switches a path back to the automatic search; a missing field leaves it unchanged. */
  save: (changes: { par2Path?: string; sevenZipPath?: string; autoInstall?: boolean }) => request<ToolSettings>('PUT', '/settings/tools', changes),
  /** Installs (or reinstalls) the tool; answers when the installation has finished. */
  install: (tool: ToolName) => request<ToolSettings>('POST', `/settings/tools/${tool}/install`),
}
