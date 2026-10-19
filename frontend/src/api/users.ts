import { request } from './client'
import type { DiscordInfo, LimitUsage } from './me'

// Users and invites (0.10); the own profile is in me.ts. Roles and states come as lowercase strings.

export type Role = 'owner' | 'admin' | 'friend' | 'requester'

export type UsageItem = LimitUsage

export interface Usage {
  movies: UsageItem
  series: UsageItem
}

export type { DiscordInfo }

export interface UserAccount {
  id: number
  username: string
  displayName: string | null
  email: string | null
  role: Role
  enabled: boolean
  createdAt: string
  lastLogin: string | null
  /** null = the role's default. */
  autoApprove: boolean | null
  effectiveAutoApprove: boolean
  movieRequestLimit: number | null
  seriesRequestLimit: number | null
  requestLimitDays: number | null
  /** Own limits instead of the defaults (requestLimitDays is set). */
  customLimits: boolean
  discord: DiscordInfo | null
  requestCount: number
  usage: Usage
}

/** Create: username and password required. Update: both ignored; the other fields replace the stored ones. */
export interface UserInput {
  username?: string
  password?: string
  role: Role
  displayName: string | null
  email: string | null
  enabled: boolean
  autoApprove: boolean | null
  movieRequestLimit: number | null
  seriesRequestLimit: number | null
  requestLimitDays: number | null
}

export type InviteRole = 'friend' | 'requester'

export interface Invite {
  id: number
  role: Role
  note: string | null
  createdBy: number
  createdByName: string | null
  created: string
  expires: string | null
  maxUses: number
  uses: number
  revoked: boolean
  state: 'active' | 'expired' | 'usedUp' | 'revoked'
}

export interface InviteList {
  records: Invite[]
  /** Invite links need the sign-in page (Settings → General → Security). */
  formsSignIn: boolean
}

export interface CreatedInvite {
  id: number
  url: string
  token: string
  invite: Invite
}

export interface InviteCheck {
  valid: boolean
  message: string | null
  role: Role | null
  note: string | null
  expires: string | null
}

export const usersKeys = {
  all: ['users'] as const,
  invites: ['invites'] as const,
}

export const usersApi = {
  list: () => request<UserAccount[]>('GET', '/users'),
  create: (input: UserInput) => request<UserAccount>('POST', '/users', input),
  update: (id: number, input: UserInput) => request<UserAccount>('PUT', `/users/${id}`, input),
  remove: (id: number) => request<void>('DELETE', `/users/${id}`),
  resetPassword: (id: number, password: string) => request<void>('POST', `/users/${id}/password`, { password }),
  revokeSessions: (id: number) => request<void>('POST', `/users/${id}/sessions/revoke`),
  unlinkDiscord: (id: number) => request<void>('DELETE', `/users/${id}/discord`),

  invites: () => request<InviteList>('GET', '/invites'),
  createInvite: (input: { role: InviteRole; note: string | null; expiresInDays: number | null; maxUses: number | null }) =>
    request<CreatedInvite>('POST', '/invites', input),
  revokeInvite: (id: number) => request<void>('DELETE', `/invites/${id}`),
  deleteInvite: (id: number) => request<void>('DELETE', `/invites/${id}?remove=true`),
  checkInvite: (token: string) => request<InviteCheck>('GET', `/invites/${encodeURIComponent(token)}`),
  acceptInvite: (token: string, username: string, password: string) =>
    request<unknown>('POST', `/invites/${encodeURIComponent(token)}/accept`, { username, password }),

}

export const roleNames: Record<Role, string> = {
  owner: 'Owner',
  admin: 'Admin',
  friend: 'Friend',
  requester: 'Requester',
}

export const roleHelp: Record<Role, string> = {
  owner: 'Everything, including other owners.',
  admin: 'Everything except owners.',
  friend: 'Library, downloads and requests; no settings.',
  requester: 'Only Discover, requests and their profile.',
}

/** "Jane Doe" or the username. */
export function nameOf(user: { displayName: string | null; username: string }): string {
  return user.displayName?.trim() || user.username
}

/** One or two letters for the avatar. */
export function initials(user: { displayName: string | null; username: string }): string {
  const parts = nameOf(user).split(/[\s._-]+/).filter(Boolean)
  return ((parts[0]?.[0] ?? '?') + (parts.length > 1 ? parts[parts.length - 1][0] : '')).toUpperCase()
}

/** "2 of 5" / "2 · no limit". */
export function usageText(item: UsageItem): string {
  return item.limit == null ? `${item.used}` : `${item.used} of ${item.limit}`
}
