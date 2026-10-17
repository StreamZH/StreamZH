import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { downloadKeys } from '../api/downloads'
import { queryKeys, type Episode } from '../api/library'
import { mediaManagementApi, mediaManagementKeys } from '../api/mediaManagement'
import { statusKeys } from '../api/status'

/** Import state of the given downloads, refreshed while one is still queued or running. */
export function useImportStates(ids: number[]) {
  const query = useQuery({
    queryKey: mediaManagementKeys.imports(ids),
    queryFn: () => mediaManagementApi.imports(ids),
    enabled: ids.length > 0,
    refetchInterval: (q) => (q.state.data?.some((i) => i.state === 'queued' || i.state === 'importing') ? 2_000 : false),
  })
  const byId = new Map((query.data ?? []).map((i) => [i.downloadId, i]))
  return (id: number) => byId.get(id) ?? null
}

/** "Import now / again" and "Dismiss"; refresh imports, downloads, library and the status window afterwards. */
export function useImportActions(onError: (title: string, message: string) => void, onDone?: (title: string) => void) {
  const queryClient = useQueryClient()
  const refresh = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: mediaManagementKeys.allImports }),
      queryClient.invalidateQueries({ queryKey: downloadKeys.all }),
      queryClient.invalidateQueries({ queryKey: statusKeys.ongoing }),
      queryClient.invalidateQueries({ queryKey: queryKeys.movies }),
      queryClient.invalidateQueries({ queryKey: queryKeys.series }),
    ])
  const importNow = useMutation({
    mutationFn: mediaManagementApi.importNow,
    onSuccess: () => onDone?.('Import queued'),
    onError: (e) => onError('Could not import', e.message),
    onSettled: refresh,
  })
  const dismiss = useMutation({
    mutationFn: mediaManagementApi.dismiss,
    onError: (e) => onError('Could not dismiss', e.message),
    onSettled: refresh,
  })
  return { importNow, dismiss, refresh }
}

/** "S01E02", "S01E02E03", "S01E02-E04" → episode ids of the series (null when a number is not in the library). */
export function parseEpisodes(text: string, episodes: Episode[]): number[] | null {
  const match = /^\s*S(\d{1,2})\s*E(\d{1,3})((?:\s*-?\s*E?\d{1,3})*)\s*$/i.exec(text)
  if (!match) return null
  const season = Number(match[1])
  const numbers = [Number(match[2])]
  for (const part of match[3].matchAll(/(-)?\s*E?(\d{1,3})/gi)) {
    const value = Number(part[2])
    if (part[1] && value > numbers[numbers.length - 1]) {
      for (let n = numbers[numbers.length - 1] + 1; n <= value; n++) numbers.push(n)
    } else numbers.push(value)
  }
  const ids = numbers.map((n) => episodes.find((e) => e.seasonNumber === season && e.episodeNumber === n)?.id)
  return ids.every((id) => id != null) ? [...new Set(ids as number[])] : null
}
