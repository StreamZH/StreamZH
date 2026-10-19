import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { setupGuideApi, setupGuideKeys, type SetupGuide, type SetupStepKey } from '../api/setupGuide'
import { useIsAdmin } from '../status/useOngoing'

/** The checklist for owners and admins (never fetched for friends and requesters); read again whenever a page shows it. */
export function useSetupGuide() {
  const admin = useIsAdmin()
  return useQuery({ queryKey: setupGuideKeys.guide, queryFn: setupGuideApi.get, enabled: admin, staleTime: 0, retry: false })
}

export function useDismissGuide() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: setupGuideApi.dismiss,
    onSuccess: (data) => queryClient.setQueryData(setupGuideKeys.guide, data),
  })
}

export const stepTitles: Record<SetupStepKey, string> = {
  storage: 'Storage locations',
  rootFolders: 'Root folders for movies and series',
  tmdb: 'TMDB key',
  language: 'Release language',
  indexers: 'Indexers',
  downloads: 'A download client',
}

/** The first step that is not done; for an admin the owner-only storage step is skipped while others are open. */
export function nextStep(guide: SetupGuide): SetupStepKey | null {
  const open = guide.steps.filter((s) => !s.done)
  const own = guide.isOwner ? open : open.filter((s) => s.key !== 'storage')
  return (own[0] ?? open[0])?.key ?? null
}

/** An example folder for the texts: D:\\Media on Windows, /data in Docker, /data/media elsewhere. */
export function examplePath(guide: SetupGuide, kind: 'storage' | 'movies' | 'series'): string {
  const base = guide.isWindows ? 'D:\\Media' : guide.isDocker ? '/data' : '/data/media'
  if (kind === 'storage') return base
  const sep = guide.isWindows ? '\\' : '/'
  return `${base}${sep}${kind === 'movies' ? 'Movies' : 'Series'}`
}
