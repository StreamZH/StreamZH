import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from 'react-router'
import { libraryApi, queryKeys, type MediaType } from '../api/library'
import { profilesApi } from '../api/profiles'

export function useDetailActions(type: MediaType, id: number) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const detailKey = type === 'Movie' ? queryKeys.movie(id) : queryKeys.seriesDetail(id)

  const monitor = useMutation({
    mutationFn: (monitored: boolean) => libraryApi.setMonitored(type, id, monitored),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: detailKey }),
  })

  const refresh = useMutation({ mutationFn: () => libraryApi.refresh(type, id) })

  const assignProfile = useMutation({
    mutationFn: (qualityProfileId: number) => profilesApi.assign(type, id, qualityProfileId),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: detailKey })
      await queryClient.invalidateQueries({ queryKey: type === 'Movie' ? queryKeys.movies : queryKeys.series })
    },
  })

  const remove = useMutation({
    mutationFn: () => libraryApi.remove(type, id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: type === 'Movie' ? queryKeys.movies : queryKeys.series })
      void navigate(type === 'Movie' ? '/library/movies' : '/library/series')
    },
  })

  return { monitor, refresh, remove, assignProfile }
}
