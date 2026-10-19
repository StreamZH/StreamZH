import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { withBase } from '../lib/urlBase'
import { downloadKeys, type DownloadProgressItem, type LiveProgress } from './downloads'
import { historyKeys } from './history'
import { indexerKeys } from './indexers'
import { queryKeys, type JobStatus } from './library'
import { releaseKeys, type SearchFinished } from './releases'
import { requestKeys } from './requests'
import { statusKeys } from './status'
import { toolKeys } from './tools'
import { invalidateWanted } from './wanted'

/**
 * Keeps one SignalR connection to /hub open, refreshes cached library data when the
 * server reports changes, and reports whether live updates are flowing.
 */
export function useLiveConnection(enabled = true): boolean {
  const queryClient = useQueryClient()
  const [connected, setConnected] = useState(false)

  useEffect(() => {
    // Requesters (0.10) connect too; the server sends them only "requestChanged" of their own requests.
    if (!enabled) return
    const connection = new HubConnectionBuilder()
      .withUrl(withBase('/hub'))
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    connection.on('libraryChanged', (event: { mediaType: 'Movie' | 'Series'; id: number | null }) => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.stats })
      void queryClient.invalidateQueries({ queryKey: queryKeys.rootFolders })
      invalidateWanted(queryClient)
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
      // Also sent when a tool installation starts or ends; refetches only while Settings → Tools is open.
      void queryClient.invalidateQueries({ queryKey: toolKeys.settings })
    })

    connection.on('downloadChanged', () => {
      void queryClient.invalidateQueries({ queryKey: downloadKeys.all })
      void queryClient.invalidateQueries({ queryKey: ['imports'] })
      invalidateWanted(queryClient)
      refetchOngoingSoon()
    })

    // Live bars between refetches: the newest progress of every active download.
    // Usenet (engine) and torrents (tracker) send their own messages: merge by id and drop entries not seen for 5 s.
    connection.on('downloadProgress', (event: { items: DownloadProgressItem[] }) => {
      const now = Date.now()
      const prev = queryClient.getQueryData<LiveProgress>(downloadKeys.live)
      const items: LiveProgress['items'] = {}
      const seen: Record<number, number> = {}
      if (prev) {
        for (const [key, item] of Object.entries(prev.items)) {
          const id = Number(key)
          const at = prev.seen?.[id] ?? prev.at
          if (now - at < 5000) {
            items[id] = item
            seen[id] = at
          }
        }
      }
      for (const item of event.items ?? []) {
        items[item.id] = item
        seen[item.id] = now
      }
      queryClient.setQueryData<LiveProgress>(downloadKeys.live, { at: now, items, seen })
    })

    // Handlers must not return a value: SignalR treats a return value as a client result and logs an error.
    connection.on('releaseGrabbed', () => {
      void queryClient.invalidateQueries({ queryKey: historyKeys.all })
    })
    connection.on('searchFinished', (event: SearchFinished) => {
      queryClient.setQueryData(releaseKeys.searchFinished(event.mediaType, event.id), { ...event, at: Date.now() })
      invalidateWanted(queryClient)
    })

    connection.on('indexersChanged', () => {
      void queryClient.invalidateQueries({ queryKey: indexerKeys.all })
    })

    // 0.10: a request was created, decided or became available: lists, the nav badge and Discover's badges.
    connection.on('requestChanged', () => {
      void queryClient.invalidateQueries({ queryKey: requestKeys.all })
      void queryClient.invalidateQueries({ queryKey: ['discover'] })
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
  }, [queryClient, enabled])

  return connected
}
