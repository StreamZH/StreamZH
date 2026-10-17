// Per-browser memory of the status window. Storage can be missing or blocked (private mode, previews): every access is guarded.

const openKey = 'streamzh-status-open'
const dismissedKey = 'streamzh-status-dismissed'
const maxDismissed = 50

export function loadOpen(): boolean {
  try {
    return localStorage.getItem(openKey) === '1'
  } catch {
    return false
  }
}

export function saveOpen(open: boolean) {
  try {
    localStorage.setItem(openKey, open ? '1' : '0')
  } catch {
    // no storage
  }
}

export function loadDismissed(): Set<string> {
  try {
    const value: unknown = JSON.parse(localStorage.getItem(dismissedKey) ?? '[]')
    return new Set(Array.isArray(value) ? value.filter((v): v is string => typeof v === 'string') : [])
  } catch {
    return new Set()
  }
}

export function saveDismissed(keys: Set<string>) {
  try {
    localStorage.setItem(dismissedKey, JSON.stringify([...keys].slice(-maxDismissed)))
  } catch {
    // no storage
  }
}
