import { useSyncExternalStore } from 'react'
import styles from './Advanced.module.css'

// "Advanced" mode: expert settings are hidden until the switch is on. One switch for the whole app (like Sonarr's
// "Show Advanced"), remembered per browser. Storage can be missing or blocked: every access is guarded.

const storageKey = 'streamzh-advanced'
const listeners = new Set<() => void>()

function read(): boolean {
  try {
    return localStorage.getItem(storageKey) === '1'
  } catch {
    return false
  }
}

let current = read()

export function setAdvanced(value: boolean) {
  current = value
  try {
    localStorage.setItem(storageKey, value ? '1' : '0')
  } catch {
    // no storage: the switch still works until the page is reloaded
  }
  listeners.forEach((listener) => listener())
}

function subscribe(listener: () => void) {
  listeners.add(listener)
  const onStorage = (event: StorageEvent) => {
    if (event.key !== storageKey) return
    current = read()
    listener()
  }
  window.addEventListener('storage', onStorage)
  return () => {
    listeners.delete(listener)
    window.removeEventListener('storage', onStorage)
  }
}

/** True while expert settings are shown. */
export function useAdvanced(): boolean {
  return useSyncExternalStore(subscribe, () => current, () => false)
}

/** Class for the label of an expert setting, so it stands out while advanced mode is on (like Sonarr). */
export const advancedLabel = styles.label
