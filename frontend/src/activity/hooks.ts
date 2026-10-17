import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback, useState } from 'react'
import { downloadKeys, type DownloadItem } from '../api/downloads'
import { libraryApi, queryKeys } from '../api/library'
import { statusKeys } from '../api/status'
import type { ToastMessage } from '../indexerTools/Toast'

/** "Dune: Part Two (2024)" for downloads that belong to a movie or series in the library, with the link to it. */
export function useMediaLinks(items: Pick<DownloadItem, 'mediaType' | 'movieId' | 'seriesId'>[]) {
  const needsMovies = items.some((i) => i.movieId != null)
  const needsSeries = items.some((i) => i.seriesId != null)
  const movies = useQuery({ queryKey: queryKeys.movies, queryFn: libraryApi.movies, enabled: needsMovies, staleTime: 60_000 })
  const series = useQuery({ queryKey: queryKeys.series, queryFn: libraryApi.series, enabled: needsSeries, staleTime: 60_000 })

  return useCallback(
    (item: Pick<DownloadItem, 'mediaType' | 'movieId' | 'seriesId'>): { to: string; label: string } | null => {
      if (item.movieId != null) {
        const movie = movies.data?.find((m) => m.id === item.movieId)
        return { to: `/library/movies/${item.movieId}`, label: movie ? `${movie.title} (${movie.year})` : 'Open movie' }
      }
      if (item.seriesId != null) {
        const show = series.data?.find((s) => s.id === item.seriesId)
        return { to: `/library/series/${item.seriesId}`, label: show ? show.title : 'Open series' }
      }
      return null
    },
    [movies.data, series.data],
  )
}

/** Small notices for the results of actions (pause, delete, upload …). */
export function useToasts() {
  const [toasts, setToasts] = useState<ToastMessage[]>([])
  const push = useCallback((tone: ToastMessage['tone'], title: string, detail?: string) => {
    setToasts((list) => [...list.slice(-3), { id: Date.now() + Math.random(), tone, title, detail }])
  }, [])
  const dismiss = useCallback((id: number) => setToasts((list) => list.filter((t) => t.id !== id)), [])
  return { toasts, push, dismiss }
}

/** A download action: refreshes the queue, history and status window afterwards and reports errors. */
export function useDownloadAction<T>(fn: (arg: T) => Promise<unknown>, onError: (message: string) => void, onSuccess?: (arg: T) => void) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: fn,
    onSuccess: (_, arg) => onSuccess?.(arg),
    onError: (error) => onError(error instanceof Error ? error.message : String(error)),
    onSettled: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: downloadKeys.all }),
        queryClient.invalidateQueries({ queryKey: statusKeys.ongoing }),
      ])
    },
  })
}
