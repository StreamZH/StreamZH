import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { importListApi, importListKeys } from '../api/importLists'
import type { MediaType } from '../api/library'
import { useIsAdmin } from '../status/useOngoing'
import styles from './ImportLists.module.css'

export interface ListItemRef {
  mediaType: MediaType
  tmdbId: number
  title: string
  year: number
}

/**
 * For "Remove" on an item an import list added: a checkbox "Add to import list exclusions" (on by default), so the
 * list does not add it again at the next sync. `item` undefined: nothing is asked (e.g. while not confirming).
 */
export function useExclusionOnRemove(item: ListItemRef | undefined) {
  const admin = useIsAdmin()
  const queryClient = useQueryClient()
  const [exclude, setExclude] = useState(true)
  const origin = useQuery({
    queryKey: importListKeys.origin(item?.mediaType ?? 'Movie', item?.tmdbId ?? 0),
    queryFn: () => importListApi.origin(item!.mediaType, item!.tmdbId),
    enabled: admin && !!item,
    retry: false,
    staleTime: 0,
  })
  const listName = item ? (origin.data?.listName ?? null) : null

  const prompt = listName ? (
    <label className={styles.removeCheck} title={`Added by the import list ${listName}`}>
      <input type="checkbox" checked={exclude} onChange={(e) => setExclude(e.target.checked)} />
      Add to import list exclusions (added by {listName})
    </label>
  ) : null

  /** Adds the exclusion when asked for; a failure never blocks the remove. */
  const apply = async () => {
    if (!item || !listName || !exclude) return
    try {
      await importListApi.addExclusion(item.mediaType, item.tmdbId, item.title, item.year)
      await queryClient.invalidateQueries({ queryKey: importListKeys.exclusions })
    } catch {
      // The item is removed anyway; the exclusion can be added from the list's preview.
    }
  }

  return { prompt, apply }
}
