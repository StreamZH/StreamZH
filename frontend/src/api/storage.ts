import { request } from './client'

// Settings → General → Storage (0.12): the base folders StreamZH may read and write media and downloads in.

export interface StorageLocation {
  path: string
  exists: boolean
  /** What is configured inside it ("Root folder: D:\Media\Movies"), at most five. */
  uses: string[]
}

export interface StorageSettings {
  locations: StorageLocation[]
  /** Only the owner adds and removes storage locations. */
  canChange: boolean
  defaultDownloadFolder: string
}

/** The API error code of a folder outside the storage locations (the owner can add it in one click). */
export const outsideStorageCode = 'outside-storage'

export const storageKeys = { storage: ['settings', 'storage'] as const }

export const storageApi = {
  get: () => request<StorageSettings>('GET', '/settings/storage'),
  add: (path: string) => request<StorageSettings>('POST', '/settings/storage', { path }),
  remove: (path: string) => request<StorageSettings>('DELETE', `/settings/storage?path=${encodeURIComponent(path)}`),
}
