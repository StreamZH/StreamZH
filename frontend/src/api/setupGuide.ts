import { request } from './client'

// 1.0 RC: the "Get started" checklist (owners and admins). Every step's state comes from the real configuration.

export type SetupStepKey = 'storage' | 'rootFolders' | 'tmdb' | 'language' | 'indexers' | 'downloads'

/**
 * Release language of the default quality profile: any (HD-1080p), original (HD-1080p), german (Deutsch 1080p) or
 * native, another language with its ISO 639-1 code ("<Language> 1080p", e.g. Turkish 1080p).
 */
export type ReleaseLanguage = 'any' | 'original' | 'german' | 'native'

/** What the release language step sends: any or original, or a language by its ISO 639-1 code ("de" means Deutsch 1080p). */
export type ReleaseLanguageChoice = { language: 'any' | 'original' } | { language: 'native'; code: string }

/** A language StreamZH has a profile for: ISO 639-1 code and English name. */
export interface LanguageOption {
  code: string
  name: string
}

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
    /** What the default quality profile asks for; "other" for a profile with a language without an ISO code. */
    releaseLanguage: ReleaseLanguage | 'other'
    /** ISO 639-1 code of the default profile's language ("de", "tr"); null for any and original. */
    releaseLanguageCode: string | null
    /** Name of the default profile's language ("German", "Turkish"); null for any and original. */
    releaseLanguageName: string | null
    defaultProfile: string | null
    /** The user's own language (browser, else the TMDB metadata language), never English; null if none fits. */
    suggestedLanguage: LanguageOption | null
  }
}

export const setupGuideKeys = { guide: ['setup-guide'] as const }

export const setupGuideApi = {
  get: () => request<SetupGuide>('GET', '/setup/guide'),
  dismiss: (dismissed: boolean) => request<SetupGuide>('PUT', '/setup/guide', { dismissed }),
  /** Sets the default quality profile for this release language (creates "<Language> 1080p" if needed). */
  language: (choice: ReleaseLanguageChoice) => request<SetupGuide>('PUT', '/setup/guide/language', choice),
}
