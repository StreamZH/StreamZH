import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, type ReactNode } from 'react'
import { api, unauthorizedEvent } from '../api/client'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import { splashDone } from '../lib/splash'
import { AuthContext, authQueryKey } from './auth'
import Brand from './Brand'
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

  // The splash screen (index.html) stays until StreamZH knows whether to show setup, sign-in or the app.
  useEffect(() => {
    if (!state.isPending) splashDone()
  }, [state.isPending])

  if (state.isPending) {
    return <div className={styles.loading} aria-busy="true" />
  }

  if (state.isError) {
    return (
      <div className={styles.screen}>
        <div className={styles.wrap}>
          <Brand />
          <div className={styles.card} role="alert">
            <div className={styles.downIcon}>
              <Icon name="alert" size={22} />
            </div>
            <div className={styles.stack}>
              <h1 className={styles.title}>Not reachable</h1>
              <p className={styles.lead}>StreamZH is not reachable. Is the service running?</p>
            </div>
            <button className={`${ui.primary} ${ui.block}`} onClick={() => void state.refetch()} disabled={state.isFetching}>
              {state.isFetching ? 'Trying…' : 'Try again'}
            </button>
          </div>
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
