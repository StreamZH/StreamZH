import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef } from 'react'
import { queryKeys, type MediaType } from '../api/library'
import { organizeApi, organizeKeys } from '../api/organize'
import { useIsAdmin } from '../status/useOngoing'

/**
 * GET /organize/status: the running organize, undo or move (polled every second while one runs), the last result and
 * whether the last organize can be undone. Refreshes the library lists when a run ends.
 */
export function useOrganizeStatus(enabled = true) {
  const admin = useIsAdmin()
  const queryClient = useQueryClient()
  const query = useQuery({
    queryKey: organizeKeys.status,
    queryFn: organizeApi.status,
    enabled: admin && enabled,
    refetchInterval: (q) => (q.state.data?.running ? 1000 : false),
  })

  const running = query.data?.running
  const wasRunning = useRef<MediaType | null>(null)
  useEffect(() => {
    if (running) {
      wasRunning.current = running.mediaType
      return
    }
    if (wasRunning.current) {
      const type = wasRunning.current
      wasRunning.current = null
      void queryClient.invalidateQueries({ queryKey: type === 'Movie' ? queryKeys.movies : queryKeys.series })
      void queryClient.invalidateQueries({ queryKey: type === 'Movie' ? ['movie'] : ['series-detail'] })
      void queryClient.invalidateQueries({ queryKey: ['history'] })
      void queryClient.invalidateQueries({ queryKey: ['rename'] })
    }
  }, [running, queryClient])

  return query
}
