import { useEffect } from 'react'

/** Scrolls to the element named in the URL hash (e.g. /settings#media) once the page has rendered. */
export function useScrollToHash() {
  useEffect(() => {
    const id = window.location.hash.slice(1)
    if (id) document.getElementById(id)?.scrollIntoView({ behavior: 'smooth', block: 'start' })
  }, [])
}
