import { useCallback, useEffect } from 'react'
import { useSearchParams } from 'react-router'

/**
 * Page and filters of a list in the URL query (?page=2&q=dune&status=failed), so back/forward and links keep them.
 * Changing a filter goes back to page 1. `filterKeys` are the query keys this list uses as filters.
 */
export function useListParams(filterKeys: readonly string[]) {
  const [url, setUrl] = useSearchParams()
  const page = Math.max(1, Math.floor(Number(url.get('page'))) || 1)
  const keys = filterKeys.join(',')

  const get = useCallback((key: string) => url.get(key) ?? '', [url])

  /** Sets (or with '' removes) filters and goes back to page 1. `replace` for typing in the search box. */
  const setFilters = useCallback(
    (changes: Record<string, string | undefined>, options?: { replace?: boolean }) =>
      setUrl(
        (previous) => {
          const next = new URLSearchParams(previous)
          for (const [key, value] of Object.entries(changes)) {
            if (value) next.set(key, value)
            else next.delete(key)
          }
          next.delete('page')
          return next
        },
        { replace: options?.replace },
      ),
    [setUrl],
  )

  const setPage = useCallback(
    (value: number, options?: { replace?: boolean }) =>
      setUrl(
        (previous) => {
          const next = new URLSearchParams(previous)
          if (value > 1) next.set('page', String(value))
          else next.delete('page')
          return next
        },
        { replace: options?.replace },
      ),
    [setUrl],
  )

  // Starting or clearing a search adds a history entry; refining it replaces the entry (no entry per pause in typing).
  const current = url.get('q') ?? ''
  const setSearch = useCallback((q: string) => setFilters({ q }, { replace: current !== '' && q !== '' }), [current, setFilters])

  const clear = useCallback(() => setFilters(Object.fromEntries(keys.split(',').map((k) => [k, undefined]))), [keys, setFilters])

  const active = filterKeys.some((key) => (url.get(key) ?? '') !== '')

  return { page, get, setFilters, setSearch, setPage, clear, active }
}

/** Moves to the last page when the current one is past the end (after a filter, a delete or a smaller list). */
export function useClampPage(page: number, total: number | undefined, pageSize: number, setPage: (page: number, options?: { replace?: boolean }) => void) {
  useEffect(() => {
    if (total == null) return
    const pages = Math.max(1, Math.ceil(total / pageSize))
    if (page > pages) setPage(pages, { replace: true })
  }, [page, total, pageSize, setPage])
}

/** Start of the day in local time, `days` days ago, as ISO (stable for the whole day, so it can be part of a query key). */
export function startOfDay(daysAgo: number): string {
  const date = new Date()
  date.setHours(0, 0, 0, 0)
  date.setDate(date.getDate() - daysAgo)
  return date.toISOString()
}
