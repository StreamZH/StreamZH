import { useSyncExternalStore } from 'react'

export type Theme = 'light' | 'dark'

const key = 'streamzh-theme'
const media = () => window.matchMedia('(prefers-color-scheme: dark)')
const listeners = new Set<() => void>()

/** The theme in use: the user's choice (data-theme on <html>) or the system's. */
export function currentTheme(): Theme {
  const chosen = document.documentElement.dataset.theme
  if (chosen === 'light' || chosen === 'dark') return chosen
  return media().matches ? 'dark' : 'light'
}

/** Switches between light and dark and remembers it (like the sun/moon button in CloudZH). */
export function toggleTheme() {
  const next: Theme = currentTheme() === 'dark' ? 'light' : 'dark'
  document.documentElement.dataset.theme = next
  try {
    localStorage.setItem(key, next)
  } catch {
    // no storage: the choice lasts until the page reloads
  }
  for (const l of listeners) l()
}

function subscribe(listener: () => void) {
  listeners.add(listener)
  const mq = media()
  mq.addEventListener('change', listener)
  return () => {
    listeners.delete(listener)
    mq.removeEventListener('change', listener)
  }
}

export function useTheme(): Theme {
  return useSyncExternalStore(subscribe, currentTheme, () => 'dark')
}
