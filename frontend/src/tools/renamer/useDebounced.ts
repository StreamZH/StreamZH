import { useEffect, useState } from 'react'

/** The value after it has not changed for `delay` ms (for live previews while typing). Use strings or numbers, not new objects. */
export function useDebounced<T>(value: T, delay = 350): T {
  const [debounced, setDebounced] = useState(value)
  useEffect(() => {
    const timer = window.setTimeout(() => setDebounced(value), delay)
    return () => window.clearTimeout(timer)
  }, [value, delay])
  return debounced
}
