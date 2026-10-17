import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { downloadKeys, type DownloadProgressItem, type LiveProgress } from './downloads'
import { historyKeys } from './history'
import { indexerKeys } from './indexers'
import { queryKeys, type JobStatus } from './library'
import { releaseKeys, type SearchFinished } from './releases'
import { statusKeys } from './status'

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

    // The status window re-reads /status/ongoing shortly after a burst of changes, not once per message.
    let ongoingTimer: number | undefined
    const refetchOngoingSoon = () => {
      window.clearTimeout(ongoingTimer)
      ongoingTimer = window.setTimeout(() => {
        void queryClient.invalidateQueries({ queryKey: statusKeys.ongoing })
      }, 800)
    }

    connection.on('jobs', (status: JobStatus) => {
      queryClient.setQueryData(queryKeys.jobs, status)
      refetchOngoingSoon()
    })

    connection.on('ongoing', () => {
      refetchOngoingSoon()
    })

    connection.on('downloadChanged', () => {
      void queryClient.invalidateQueries({ queryKey: downloadKeys.all })
      refetchOngoingSoon()
    })

    // Live bars between refetches: the newest progress of every active download.
    connection.on('downloadProgress', (event: { items: DownloadProgressItem[] }) => {
      const items: LiveProgress['items'] = {}
      for (const item of event.items ?? []) items[item.id] = item
      queryClient.setQueryData<LiveProgress>(downloadKeys.live, { at: Date.now(), items })
    })

    // Handlers must not return a value: SignalR treats a return value as a client result and logs an error.
    connection.on('releaseGrabbed', () => {
      void queryClient.invalidateQueries({ queryKey: historyKeys.all })
    })
    connection.on('searchFinished', (event: SearchFinished) => {
      queryClient.setQueryData(releaseKeys.searchFinished(event.mediaType, event.id), { ...event, at: Date.now() })
    })

    connection.on('indexersChanged', () => {
      void queryClient.invalidateQueries({ queryKey: indexerKeys.all })
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
      window.clearTimeout(ongoingTimer)
      void connection.stop()
    }
  }, [queryClient])

  return connected
}
