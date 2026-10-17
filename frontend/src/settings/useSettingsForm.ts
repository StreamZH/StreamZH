import { useMutation, useQuery, useQueryClient, type QueryKey } from '@tanstack/react-query'
import { useEffect, useMemo, useState } from 'react'
import { ApiError } from '../api/client'

/**
 * Draft handling for one settings section: loads the values, keeps a local draft, saves it and maps field errors.
 * Fields changed since the last save hide their old error. `saved` is true for a moment after a successful save.
 */
export function useSettingsForm<T extends object>({
  queryKey,
  load,
  save,
  invalidate = [],
}: {
  queryKey: QueryKey
  load: () => Promise<T>
  /** Gets the whole draft; returns the saved values (or nothing, then the query is reloaded). */
  save: (draft: T) => Promise<T | void>
  /** More queries to refresh after saving. */
  invalidate?: QueryKey[]
}) {
  const queryClient = useQueryClient()
  const query = useQuery({ queryKey, queryFn: load })
  const [draft, setDraft] = useState<T | null>(null)
  const [edited, setEdited] = useState<Set<string>>(new Set())
  const [saved, setSaved] = useState(false)

  const current = draft ?? query.data ?? null

  const mutation = useMutation({
    mutationFn: () => save(current as T),
    onSuccess: async (result) => {
      if (result) queryClient.setQueryData(queryKey, result)
      else await queryClient.invalidateQueries({ queryKey })
      await Promise.all(invalidate.map((key) => queryClient.invalidateQueries({ queryKey: key })))
      setDraft(null)
      setEdited(new Set())
      setSaved(true)
    },
  })

  useEffect(() => {
    if (!saved) return
    const timer = setTimeout(() => setSaved(false), 2500)
    return () => clearTimeout(timer)
  }, [saved])

  const dirty = useMemo(() => draft !== null && query.data !== undefined && JSON.stringify(draft) !== JSON.stringify(query.data), [draft, query.data])
  const fieldErrors = mutation.error instanceof ApiError ? mutation.error.fieldErrors : {}

  return {
    query,
    /** The values to show: the draft while editing, otherwise the loaded values. */
    values: current,
    set: (change: Partial<T>) => {
      setDraft((d) => ({ ...((d ?? query.data) as T), ...change }))
      setEdited((e) => new Set([...e, ...Object.keys(change)]))
      setSaved(false)
    },
    dirty,
    reset: () => {
      setDraft(null)
      setEdited(new Set())
      mutation.reset()
    },
    save: () => {
      setEdited(new Set())
      mutation.mutate()
    },
    saving: mutation.isPending,
    saved,
    /** Error of one field (camelCase name), hidden once the field was changed again. */
    error: (field: string): string | undefined => (edited.has(field) ? undefined : fieldErrors[field]?.[0]),
    /** True while an error is shown for one of these fields (e.g. to show a hidden expert field anyway). */
    hasError: (...fields: string[]) => fields.some((f) => !edited.has(f) && fieldErrors[f] !== undefined),
    /** A save error that belongs to no field. */
    formError: mutation.error && Object.keys(fieldErrors).length === 0 ? mutation.error.message : null,
  }
}

export type SettingsForm<T extends object> = ReturnType<typeof useSettingsForm<T>>
