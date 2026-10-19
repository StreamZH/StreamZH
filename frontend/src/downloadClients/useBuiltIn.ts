import { useMutation, useQueryClient } from '@tanstack/react-query'
import { downloadClientKeys, downloadClientsApi, newClient, type ClientImplementation, type DownloadClient } from '../api/downloadClients'

/** "Turn on" the built-in torrent engine: adds the "StreamZH" client with the schema's defaults. */
export function useTurnOnBuiltIn(type: ClientImplementation | null, clients: DownloadClient[]) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => {
      if (!type) throw new Error('This server has no built-in torrent engine.')
      return downloadClientsApi.create(newClient(type, clients))
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: downloadClientKeys.all }),
  })
}
