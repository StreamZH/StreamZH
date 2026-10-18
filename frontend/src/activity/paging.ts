import { useCallback, useSyncExternalStore } from 'react'

// Paging of lists. Items per page of a paged list, remembered per browser and per list ("activity-history", …). Storage can be
// missing or blocked: every access is guarded and the default applies.

export const pageSizes = [25, 50, 100, 200] as const
export const defaultPageSize = 50

const prefix = 'streamzh-page-size:'
const listeners = new Set<() => void>()
const memory = new Map<string, number>()

function valid(value: number): boolean {
  return (pageSizes as readonly number[]).includes(value)
}

function read(list: string): number {
  const remembered = memory.get(list)
  if (remembered != null) return remembered
  let value = defaultPageSize
  try {
    const stored = Number(localStorage.getItem(prefix + list))
    if (valid(stored)) value = stored
  } catch {
    // no storage: the default
  }
  memory.set(list, value)
  return value
}

export function setPageSize(list: string, size: number) {
  if (!valid(size)) return
  memory.set(list, size)
  try {
    localStorage.setItem(prefix + list, String(size))
  } catch {
    // no storage: the size still applies until the page is reloaded
  }
  listeners.forEach((listener) => listener())
}

function subscribe(listener: () => void) {
  listeners.add(listener)
  const onStorage = (event: StorageEvent) => {
    if (!event.key?.startsWith(prefix)) return
    memory.delete(event.key.slice(prefix.length))
    listener()
  }
  window.addEventListener('storage', onStorage)
  return () => {
    listeners.delete(listener)
    window.removeEventListener('storage', onStorage)
  }
}

/** The page size of a list and a setter; 50 until the viewer picks another one. */
export function usePageSize(list: string): [number, (size: number) => void] {
  const size = useSyncExternalStore(
    subscribe,
    () => read(list),
    () => defaultPageSize,
  )
  const set = useCallback((value: number) => setPageSize(list, value), [list])
  return [size, set]
}

/** 1 … 4 5 6 … 12: the first and last page, the neighbours of the current one, gaps of one page filled in. */
export function pageList(page: number, pages: number): (number | 'gap')[] {
  if (pages <= 7) return Array.from({ length: pages }, (_, i) => i + 1)
  const shown = new Set([1, pages, page - 1, page, page + 1])
  if (page <= 4) [2, 3, 4, 5].forEach((p) => shown.add(p))
  if (page >= pages - 3) [pages - 4, pages - 3, pages - 2, pages - 1].forEach((p) => shown.add(p))
  const sorted = [...shown].filter((p) => p >= 1 && p <= pages).sort((a, b) => a - b)
  const result: (number | 'gap')[] = []
  for (const p of sorted) {
    const previous = result[result.length - 1]
    if (typeof previous === 'number' && p - previous === 2) result.push(p - 1)
    else if (typeof previous === 'number' && p - previous > 2) result.push('gap')
    result.push(p)
  }
  return result
}
