import { request } from './client'

// Settings → Discord (0.10): the built-in bot that replaces Requestrr.

export const maskedToken = '********'

export type DiscordBotState = 'off' | 'connecting' | 'connected' | 'reconnecting' | 'error'

export type SeriesSeasonsMode = 'choose' | 'all' | 'single'

export interface DiscordStatus {
  state: DiscordBotState
  botName: string | null
  botId: string | null
  guilds: number
  error: string | null
  since: string | null
  nextRetry: string | null
  permissionProblem: string | null
}

export interface DiscordSettings {
  enabled: boolean
  hasToken: boolean
  /** "********" when a token is saved (send it back to keep it), "" to remove it, or a new token. */
  botToken: string
  applicationId: string | null
  inviteUrl: string | null
  guildIds: string[]
  channelIds: string[]
  allowGuests: boolean
  guestRoleIds: string[]
  notifyChannelId: string | null
  dmRequesters: boolean
  allowDirectMessages: boolean
  movieCommand: string
  tvCommand: string
  seriesSeasons: SeriesSeasonsMode
  statusText: string
  status: DiscordStatus
  /** False only in builds without the requests module. */
  requestsReady: boolean
}

export interface DiscordTestGuild {
  id: string
  name: string
  channels: { id: string; name: string }[]
  roles: { id: string; name: string }[]
  error: string | null
}

export interface DiscordTestResult {
  ok: boolean
  botName: string | null
  botId: string | null
  applicationId: string | null
  inviteUrl: string | null
  guilds: DiscordTestGuild[]
  error: string | null
}

export interface RequestrrImportRow {
  field: string
  label: string
  current: string
  imported: string
}

export interface RequestrrImportResult {
  rows: RequestrrImportRow[]
  warnings: string[]
  hasToken: boolean
  takesToken: boolean
  version: string | null
  applied: boolean
  settings: DiscordSettings | null
}

export const discordKeys = {
  settings: ['settings', 'discord'] as const,
  status: ['settings', 'discord', 'status'] as const,
}

export const discordApi = {
  get: () => request<DiscordSettings>('GET', '/settings/discord'),
  save: (settings: DiscordSettings) =>
    request<DiscordSettings>('PUT', '/settings/discord', {
      enabled: settings.enabled,
      botToken: settings.botToken,
      applicationId: settings.applicationId ?? '',
      guildIds: settings.guildIds,
      channelIds: settings.channelIds,
      allowGuests: settings.allowGuests,
      guestRoleIds: settings.guestRoleIds,
      notifyChannelId: settings.notifyChannelId ?? '',
      dmRequesters: settings.dmRequesters,
      allowDirectMessages: settings.allowDirectMessages,
      movieCommand: settings.movieCommand,
      tvCommand: settings.tvCommand,
      seriesSeasons: settings.seriesSeasons,
      statusText: settings.statusText,
    }),
  status: () => request<DiscordStatus>('GET', '/settings/discord/status'),
  /** Without a token (or with the mask) the saved token is tested. Nothing is saved. */
  test: (botToken?: string) => request<DiscordTestResult>('POST', '/settings/discord/test', { botToken: botToken ?? null }),
  importRequestrr: (settings: string, takeToken: boolean, apply: boolean) =>
    request<RequestrrImportResult>('POST', '/settings/discord/import-requestrr', { settings, takeToken, apply }),
}
