import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from 'react-router'
import { libraryApi, queryKeys, type MediaType } from '../api/library'

export function useDetailActions(type: MediaType, id: number) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const detailKey = type === 'Movie' ? queryKeys.movie(id) : queryKeys.seriesDetail(id)

  const monitor = useMutation({
    mutationFn: (monitored: boolean) => libraryApi.setMonitored(type, id, monitored),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: detailKey }),
  })

  const refresh = useMutation({ mutationFn: () => libraryApi.refresh(type, id) })

  const remove = useMutation({
    mutationFn: () => libraryApi.remove(type, id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: type === 'Movie' ? queryKeys.movies : queryKeys.series })
      void navigate(type === 'Movie' ? '/library/movies' : '/library/series')
    },
  })

  return { monitor, refresh, remove }
}
