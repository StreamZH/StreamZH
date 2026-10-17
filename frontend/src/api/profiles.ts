import { useQuery } from '@tanstack/react-query'
import { request } from './client'
import type { MediaType } from './library'

export interface Quality {
  id: number
  name: string
  source: string
  resolution: number
  modifier: string
}

export interface Language {
  id: number
  name: string
}

export interface QualityProfileItem {
  qualityId: number
  name: string
  allowed: boolean
}

export interface QualityProfile {
  id: number
  name: string
  items: QualityProfileItem[]
  cutoffQualityId: number
  upgradeAllowed: boolean
  languageId: number
  preferDualLanguage: boolean
}

export interface QualityDefinition {
  qualityId: number
  title: string
  minSize: number
  maxSize: number | null
  preferredSize: number | null
}

export interface ProfileSettings {
  defaultQualityProfileId: number
}

const itemPath = (type: MediaType) => (type === 'Movie' ? 'movies' : 'series')

export const profilesApi = {
  qualities: () => request<Quality[]>('GET', '/qualities'),
  languages: () => request<Language[]>('GET', '/languages'),

  profiles: () => request<QualityProfile[]>('GET', '/qualityprofiles'),
  schema: () => request<QualityProfile>('GET', '/qualityprofiles/schema'),
  create: (profile: QualityProfile) => request<QualityProfile>('POST', '/qualityprofiles', profile),
  update: (profile: QualityProfile) => request<QualityProfile>('PUT', `/qualityprofiles/${profile.id}`, profile),
  remove: (id: number) => request<void>('DELETE', `/qualityprofiles/${id}`),

  definitions: () => request<QualityDefinition[]>('GET', '/qualitydefinitions'),
  saveDefinitions: (definitions: QualityDefinition[]) => request<QualityDefinition[]>('PUT', '/qualitydefinitions', definitions),

  settings: () => request<ProfileSettings>('GET', '/settings/profiles'),
  saveSettings: (defaultQualityProfileId: number) =>
    request<ProfileSettings>('PUT', '/settings/profiles', { defaultQualityProfileId }),

  assign: (type: MediaType, id: number, qualityProfileId: number) =>
    request<void>('PUT', `/${itemPath(type)}/${id}/profile`, { qualityProfileId }),
}

export const profileKeys = {
  qualities: ['qualities'] as const,
  languages: ['languages'] as const,
  profiles: ['quality-profiles'] as const,
  definitions: ['quality-definitions'] as const,
  settings: ['profile-settings'] as const,
}

/** Profiles sorted by name plus the default profile id, for selects in the library. */
export function useProfileChoices() {
  const profiles = useQuery({ queryKey: profileKeys.profiles, queryFn: profilesApi.profiles, staleTime: 60_000 })
  const settings = useQuery({ queryKey: profileKeys.settings, queryFn: profilesApi.settings, staleTime: 60_000 })
  return {
    profiles: profiles.data ?? [],
    defaultId: settings.data?.defaultQualityProfileId ?? null,
    error: profiles.error ?? settings.error,
  }
}

export const languageIds = { any: -1, original: -2 } as const
