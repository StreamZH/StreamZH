import { useQuery } from '@tanstack/react-query'
import { request } from './client'

// The signed-in user's own profile (/api/v1/me): role, permissions, request limits with their usage, the Discord link,
// and the profile page's edits (display name, e-mail, password, Discord link code).

export interface LimitUsage {
  used: number
  /** null or 0: no limit. */
  limit: number | null
  days: number | null
}

export interface DiscordInfo {
  userId: string
  name: string | null
  linked: string
}

export interface LinkCode {
  code: string
  expires: string
}

export interface Me {
  id: number
  username: string
  displayName: string | null
  email: string | null
  role: 'owner' | 'admin' | 'friend' | 'requester'
  createdAt: string | null
  lastLogin: string | null
  /** False for the owner without account (API key, local network, External sign-in): nothing to edit. */
  hasAccount: boolean
  permissions: { canRequest: boolean; canManageLibrary: boolean; isAdmin: boolean }
  /** Effective: requests are approved without an admin. */
  autoApprove: boolean
  usage: { movies: LimitUsage; series: LimitUsage }
  discord: DiscordInfo | null
  /** The Discord link code that is still valid. */
  linkCode: LinkCode | null
}

export const meKeys = { me: ['me'] as const }

export const meApi = {
  me: () => request<Me>('GET', '/me'),
  save: (displayName: string | null, email: string | null) => request<Me>('PUT', '/me', { displayName, email }),
  changePassword: (currentPassword: string, newPassword: string) => request<void>('POST', '/me/password', { currentPassword, newPassword }),
  linkCode: () => request<LinkCode>('POST', '/me/discord/link-code'),
  unlinkDiscord: () => request<void>('DELETE', '/me/discord'),
}

export function useMe() {
  return useQuery({ queryKey: meKeys.me, queryFn: meApi.me, staleTime: 30_000, retry: false })
}

/** "2 of 5 movies this week", "1 of 3 series in 30 days"; null without a limit. */
export function limitText(usage: LimitUsage | undefined, noun: 'movies' | 'series'): string | null {
  if (!usage || !usage.limit) return null
  const days = usage.days ?? 7
  const span = days === 7 ? 'this week' : days === 1 ? 'today' : days === 30 ? 'this month' : `in ${days} days`
  return `${usage.used} of ${usage.limit} ${usage.limit === 1 && noun === 'movies' ? 'movie' : noun} ${span}`
}
