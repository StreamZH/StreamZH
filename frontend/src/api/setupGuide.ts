import { request } from './client'

// 1.0 RC: the "Get started" checklist (owners and admins). Every step's state comes from the real configuration.

export type SetupStepKey = 'storage' | 'rootFolders' | 'tmdb' | 'language' | 'indexers' | 'downloads'

/** Release language of the default quality profile: any (HD-1080p), original (HD-1080p) or german (Deutsch 1080p). */
export type ReleaseLanguage = 'any' | 'original' | 'german'

export interface SetupStep {
  key: SetupStepKey
  done: boolean
  /** Locations, root folders, indexers or download clients there are; 0 for TMDB. */
  count: number
}

export interface SetupGuide {
  /** Hidden with "Hide this guide" (for the whole instance). */
  dismissed: boolean
  /** Every required step is done. */
  complete: boolean
  done: number
  total: number
  /** Storage locations are owner-only: an admin sees that step as "ask the owner". */
  isOwner: boolean
  isWindows: boolean
  isDocker: boolean
  steps: SetupStep[]
  details: {
    /** The owner's storage locations besides the default download folder. */
    storageLocations: string[]
    movieRootFolders: number
    seriesRootFolders: number
    usenetServers: number
    torrentClients: number
    builtInTorrent: boolean
    migrated: boolean
    /** Accounts, the owner included. */
    users: number
    invites: number
    connections: number
    requestsEnabled: boolean
    discordEnabled: boolean
    /** What the default quality profile asks for; "other" for a profile with another language. */
    releaseLanguage: ReleaseLanguage | 'other'
    defaultProfile: string | null
  }
}

export const setupGuideKeys = { guide: ['setup-guide'] as const }

export const setupGuideApi = {
  get: () => request<SetupGuide>('GET', '/setup/guide'),
  dismiss: (dismissed: boolean) => request<SetupGuide>('PUT', '/setup/guide', { dismissed }),
  /** Sets the default quality profile for this release language. */
  language: (language: ReleaseLanguage) => request<SetupGuide>('PUT', '/setup/guide/language', { language }),
}
