import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { useEffect, useState } from 'react'

/** Keeps one SignalR connection to /hub open and reports whether live updates are flowing. */
export function useLiveConnection(): boolean {
  const [connected, setConnected] = useState(false)

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl('/hub')
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    connection.onreconnecting(() => setConnected(false))
    connection.onreconnected(() => setConnected(true))
    connection.onclose(() => setConnected(false))

    connection
      .start()
      .then(() => setConnected(connection.state === HubConnectionState.Connected))
      .catch(() => setConnected(false))

    return () => {
      void connection.stop()
    }
  }, [])

  return connected
}
