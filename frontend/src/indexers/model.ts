import { genericDefinitionKey, type Indexer, type IndexerDefinition, type IndexerStatus } from '../api/indexers'

export function hostOf(url: string) {
  try {
    return new URL(url).host
  } catch {
    return url
  }
}

/** Time today, or "Oct 17, 14:05" on another day; "3 minutes ago". Both follow Settings → UI (see lib/format). */
export { formatAgo, formatUntil } from '../lib/format'

export function usesList(i: Indexer): string[] {
  return [i.enableRss && 'RSS', i.enableAutomaticSearch && 'Automatic', i.enableInteractiveSearch && 'Interactive'].filter(
    (u): u is string => Boolean(u),
  )
}

export type RowState = 'enabled' | 'disabled' | 'error'

export function rowState(i: Indexer): RowState {
  if (!i.enable) return 'disabled'
  if (i.status && (!i.status.healthy || i.status.limitReached)) return 'error'
  return 'enabled'
}

export interface Usage {
  text: string
  /** 0..1 when a limit is set. */
  ratio: number | null
}

function windowLabel(i: Indexer) {
  return i.limitsUnit === 'hour' ? 'this hour' : 'today'
}

function usage(used: number, limit: number | null, i: Indexer, noun: string): Usage {
  if (limit && limit > 0) {
    const label = noun.charAt(0).toUpperCase() + noun.slice(1)
    return { text: `${label} ${used}/${limit} ${windowLabel(i)}`, ratio: Math.min(used / limit, 1) }
  }
  const word = used === 1 ? noun.replace(/ies$/, 'y').replace(/s$/, '') : noun
  return { text: `${used} ${word} ${windowLabel(i)}`, ratio: null }
}

export function queryUsage(i: Indexer, status: IndexerStatus | null = i.status): Usage {
  return usage(status?.queriesUsed ?? 0, i.queryLimit, i, 'queries')
}

export function grabUsage(i: Indexer, status: IndexerStatus | null = i.status): Usage {
  return usage(status?.grabsUsed ?? 0, i.grabLimit, i, 'grabs')
}

export interface VipState {
  tone: 'warn' | 'error'
  text: string
}

/** Warning from 7 days before the VIP expiration, error once it has expired. */
export function vipState(status: IndexerStatus | null): VipState | null {
  const days = status?.vipExpiresInDays
  if (days == null || days > 7) return null
  if (days < 0) return { tone: 'error', text: 'VIP expired' }
  if (days === 0) return { tone: 'warn', text: 'VIP expires today' }
  return { tone: 'warn', text: `VIP expires in ${days} day${days === 1 ? '' : 's'}` }
}

/** Movie (2000) or TV (5000) categories for a new indexer from a catalog entry. */
function presetCategories(definition: IndexerDefinition, base: number, defaults: number[]): number[] {
  // The catalog does not know this site's categories: start from the usual ones, Test loads the site's own list.
  if (definition.categories.length === 0) return defaults
  const inRange = definition.categories.filter((id) => id >= base && id < base + 1000)
  if (inRange.length === 0) return []
  // Only the top-level category listed: the schema defaults are the useful sub-categories of it.
  if (inRange.every((id) => id === base)) return defaults
  return inRange
}

/** New indexer prefilled from a catalog entry, on top of the server's schema defaults. */
export function draftFromDefinition(schema: Indexer, definition: IndexerDefinition): Indexer {
  const generic = definition.key === genericDefinitionKey
  return {
    ...schema,
    id: 0,
    name: generic ? '' : definition.name,
    implementation: definition.implementation || schema.implementation,
    baseUrl: definition.baseUrl,
    apiPath: definition.apiPath || schema.apiPath,
    definitionKey: definition.key,
    categories: generic ? schema.categories : presetCategories(definition, 2000, schema.categories),
    tvCategories: generic ? schema.tvCategories : presetCategories(definition, 5000, schema.tvCategories),
    status: null,
  }
}

/** Copy of a saved indexer for "Clone". The stored API key never reaches the browser, so it has to be entered again. */
export function cloneOf(i: Indexer): Indexer {
  return { ...i, id: 0, name: `${i.name} (copy)`, apiKey: '', status: null }
}

/** Newznab endpoint StreamZH calls: base URL + API path. */
export function newznabUrl(i: Pick<Indexer, 'baseUrl' | 'apiPath'>) {
  const base = i.baseUrl.replace(/\/+$/, '')
  const path = i.apiPath ? (i.apiPath.startsWith('/') ? i.apiPath : `/${i.apiPath}`) : ''
  return `${base}${path}`
}
