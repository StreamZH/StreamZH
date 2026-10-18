import { useQuery } from '@tanstack/react-query'
import type { DownloadItem } from '../api/downloads'
import { downloadSettingsApi, downloadSettingsKeys } from '../api/downloadSettings'
import type { FilterOption } from './Filters'

/** Movies / series / other for download lists; custom download categories come from {@link useCustomCategories}. */
export const downloadTypeOptions: FilterOption[] = [
  { value: '', label: 'All types' },
  { value: 'movie', label: 'Movies' },
  { value: 'series', label: 'Series' },
  { value: 'other', label: 'Other' },
]

/** Movies / series for history events and the blocklist. */
export const mediaTypeOptions: FilterOption[] = [
  { value: '', label: 'Movies and series' },
  { value: 'movie', label: 'Movies' },
  { value: 'series', label: 'Series' },
]

/** The download categories added in Settings → Downloads (not movies, series, other), as filter options. */
export function useCustomCategories(): FilterOption[] {
  const choices = useQuery({ queryKey: downloadSettingsKeys.choices, queryFn: downloadSettingsApi.choices, staleTime: 60_000, retry: false })
  return (choices.data ?? []).filter((c) => !c.isDefault).map((c) => ({ value: c.name, label: c.name }))
}

/** Value of a select from the URL, or '' when the URL holds something the select does not offer. */
export function pick<T extends string>(value: string, allowed: readonly FilterOption[]): T | '' {
  return allowed.some((o) => o.value === value) ? (value as T) : ''
}

/** True when the download belongs to the type filter: '' all, movie/series/other the kind, anything else a category name. */
export function matchesType(item: Pick<DownloadItem, 'category' | 'categoryName'>, type: string): boolean {
  if (!type) return true
  if (type === 'movie' || type === 'series' || type === 'other') return item.category === type
  return (item.categoryName ?? '').toLowerCase() === type.toLowerCase()
}

/** True when every word of the search appears in the text (case-insensitive), like the server-side search. */
export function matchesSearch(text: string, search: string): boolean {
  const words = search.trim().toLowerCase().split(/\s+/).filter(Boolean)
  if (words.length === 0) return true
  const value = text.toLowerCase()
  return words.every((w) => value.includes(w))
}
