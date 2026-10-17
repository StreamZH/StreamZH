import type { DownloadItem } from '../api/downloads'

export const categoryLabels: Record<DownloadItem['category'], string> = { movie: 'Movie', series: 'Series', other: 'Other' }

export const statusLabels: Record<DownloadItem['status'], string> = {
  queued: 'Queued',
  downloading: 'Downloading',
  paused: 'Paused',
  verifying: 'Verifying',
  repairing: 'Repairing',
  extracting: 'Extracting',
  completed: 'Completed',
  failed: 'Failed',
}
