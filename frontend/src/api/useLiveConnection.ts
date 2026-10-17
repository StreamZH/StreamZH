import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { queryKeys, type JobStatus } from './library'

/**
 * Keeps one SignalR connection to /hub open, refreshes cached library data when the
 * server reports changes, and reports whether live updates are flowing.
 */
export function useLiveConnection(): boolean {
  const queryClient = useQueryClient()
  const [connected, setConnected] = useState(false)

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl('/hub')
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    connection.on('libraryChanged', (event: { mediaType: 'Movie' | 'Series'; id: number | null }) => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.stats })
      void queryClient.invalidateQueries({ queryKey: queryKeys.rootFolders })
      if (event.mediaType === 'Movie') {
        void queryClient.invalidateQueries({ queryKey: queryKeys.movies })
        if (event.id) void queryClient.invalidateQueries({ queryKey: queryKeys.movie(event.id) })
      } else {
        void queryClient.invalidateQueries({ queryKey: queryKeys.series })
        if (event.id) void queryClient.invalidateQueries({ queryKey: queryKeys.seriesDetail(event.id) })
      }
    })

    connection.on('jobs', (status: JobStatus) => {
      queryClient.setQueryData(queryKeys.jobs, status)
    })

    connection.onreconnecting(() => setConnected(false))
    connection.onreconnected(() => {
      setConnected(true)
      void queryClient.invalidateQueries()
    })
    connection.onclose(() => setConnected(false))

    connection
      .start()
      .then(() => setConnected(connection.state === HubConnectionState.Connected))
      .catch(() => setConnected(false))

    return () => {
      void connection.stop()
    }
  }, [queryClient])

  return connected
}
