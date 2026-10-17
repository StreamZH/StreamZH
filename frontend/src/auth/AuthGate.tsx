import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, type ReactNode } from 'react'
import { api, unauthorizedEvent } from '../api/client'
import { AuthContext, authQueryKey } from './auth'
import LoginPage from './LoginPage'
import SetupPage from './SetupPage'
import styles from './Auth.module.css'

/** Shows first-run setup, the login page, or the app, depending on the session. */
export default function AuthGate({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()
  const state = useQuery({ queryKey: authQueryKey, queryFn: api.authState, staleTime: Infinity })

  useEffect(() => {
    const onUnauthorized = () => void queryClient.invalidateQueries({ queryKey: authQueryKey })
    window.addEventListener(unauthorizedEvent, onUnauthorized)
    return () => window.removeEventListener(unauthorizedEvent, onUnauthorized)
  }, [queryClient])

  useEffect(() => {
    if (state.data) {
      document.title = state.data.instanceName
    }
  }, [state.data])

  if (state.isPending) {
    return <div className={styles.loading} aria-busy="true" />
  }

  if (state.isError) {
    return (
      <div className={styles.screen}>
        <div className={styles.card}>
          <p className={styles.error}>StreamZH is not reachable. Is the service running?</p>
          <button className={styles.primary} onClick={() => void state.refetch()}>
            Try again
          </button>
        </div>
      </div>
    )
  }

  if (state.data.setupRequired) {
    return <SetupPage />
  }

  if (!state.data.authenticated) {
    return <LoginPage instanceName={state.data.instanceName} />
  }

  return <AuthContext.Provider value={state.data}>{children}</AuthContext.Provider>
}
