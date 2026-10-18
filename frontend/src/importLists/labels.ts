import type { CleanLibraryLevel, ImportListGroup, PreviewState } from '../api/importLists'
import type { MediaType } from '../api/library'

export const groupLabels: Record<ImportListGroup, string> = {
  tmdb: 'TMDB',
  plex: 'Plex',
  trakt: 'Trakt',
  other: 'Other',
}

export const mediaLabel = (m: MediaType) => (m === 'Series' ? 'Series' : 'Movies')

export const stateLabels: Record<PreviewState, string> = {
  new: 'Will be added',
  waiting: 'Waiting',
  inLibrary: 'In library',
  excluded: 'Excluded',
  notFound: 'Not on TMDB',
}

export const cleanLevelOptions: { value: CleanLibraryLevel; label: string }[] = [
  { value: 'disabled', label: 'Disabled' },
  { value: 'logOnly', label: 'Log only' },
  { value: 'keepAndUnmonitor', label: 'Keep and unmonitor' },
  { value: 'removeAndKeepFiles', label: 'Remove, keep files' },
]

export const cleanLevelHelp: Record<CleanLibraryLevel, string> = {
  disabled: 'Items stay when they leave a list.',
  logOnly: 'Only writes to the log what would happen.',
  keepAndUnmonitor: 'Items stay in the library but are no longer monitored.',
  removeAndKeepFiles: 'Items are removed from the library. Their files stay on disk.',
}

/** "21:30" today, else "18 Oct 21:30". */
export function formatTime(iso: string | null) {
  if (!iso) return null
  const date = new Date(iso)
  const today = new Date().toDateString() === date.toDateString()
  return today
    ? date.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
    : date.toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })
}

export const tmdbUrl = (mediaType: MediaType, tmdbId: number) => `https://www.themoviedb.org/${mediaType === 'Series' ? 'tv' : 'movie'}/${tmdbId}`
