import { useMutation, useQuery } from '@tanstack/react-query'
import { useEffect, useState, useSyncExternalStore } from 'react'
import { useLocation } from 'react-router'
import { generalApi, generalKeys, type GeneralSettings } from '../api/general'
import Icon from '../components/Icon'
import Loader from '../components/Loader'
import ui from '../components/ui.module.css'
import { urlBase } from '../lib/urlBase'
import overlay from './Restart.module.css'
import styles from './System.module.css'

// Restart and shutdown. The overlay lives outside the pages (see RestartOverlay in main.tsx): the banner with the
// Restart button disappears as soon as the restarted StreamZH no longer needs a restart.

type Phase = 'idle' | 'restarting' | 'stopped' | 'timeout'
interface Target {
  origin: string
  base: string
  path: string
}

let state: { phase: Phase; target: Target | null } = { phase: 'idle', target: null }
const listeners = new Set<() => void>()

function setState(next: typeof state) {
  state = next
  listeners.forEach((listener) => listener())
}

function useRestartState() {
  return useSyncExternalStore(
    (listener) => {
      listeners.add(listener)
      return () => listeners.delete(listener)
    },
    () => state,
  )
}

/**
 * Where StreamZH answers after a restart: a new URL base, and a new port when the page was opened directly (not through a
 * reverse proxy) and --urls does not decide.
 */
function nextAddress(settings: GeneralSettings | undefined, path: string): Target {
  const here = window.location
  const base = settings ? settings.urlBase : urlBase
  let origin = here.origin
  const direct = here.port !== '' && (settings?.listening.map(portOf) ?? []).includes(Number(here.port))
  if (settings && !settings.hostOverride && direct) {
    const port = here.protocol === 'https:' && settings.enableSsl ? settings.sslPort : settings.port
    origin = `${here.protocol}//${here.hostname}:${port}`
  }
  return { origin, base, path }
}

function portOf(address: string): number | null {
  const match = /:(\d+)\/?$/.exec(address)
  return match ? Number(match[1]) : null
}

async function reachable(url: string): Promise<boolean> {
  try {
    const sameOrigin = new URL(url).origin === window.location.origin
    const response = await fetch(url, { cache: 'no-store', mode: sameOrigin ? 'same-origin' : 'no-cors' })
    return sameOrigin ? response.ok : true
  } catch {
    return false
  }
}

/** Restart and Shut down buttons with confirmation. */
export function RestartControls({ compact = false }: { compact?: boolean }) {
  const [confirm, setConfirm] = useState<'restart' | 'shutdown' | null>(null)
  const location = useLocation()
  const settings = useQuery({ queryKey: generalKeys.general, queryFn: generalApi.general })
  const restart = useMutation({
    mutationFn: generalApi.restart,
    onSuccess: () => setState({ phase: 'restarting', target: nextAddress(settings.data, location.pathname + location.search) }),
  })
  const shutdown = useMutation({ mutationFn: generalApi.shutdown, onSuccess: () => setState({ phase: 'stopped', target: null }) })
  const error = restart.error ?? shutdown.error

  return (
    <div className={styles.actions}>
      {confirm === 'restart' ? (
        <>
          {!compact && <span className={styles.muted}>Downloads pause and continue after the restart.</span>}
          <button className={ui.primary} type="button" onClick={() => restart.mutate()} disabled={restart.isPending}>
            Yes, restart
          </button>
          <button className={ui.button} type="button" onClick={() => setConfirm(null)}>
            Cancel
          </button>
        </>
      ) : confirm === 'shutdown' ? (
        <>
          {!compact && <span className={styles.muted}>StreamZH stops until it is started again.</span>}
          <button className={ui.danger} type="button" onClick={() => shutdown.mutate()} disabled={shutdown.isPending}>
            Yes, shut down
          </button>
          <button className={ui.button} type="button" onClick={() => setConfirm(null)}>
            Cancel
          </button>
        </>
      ) : (
        <>
          <button className={ui.button} type="button" onClick={() => setConfirm('restart')}>
            Restart
          </button>
          <button className={ui.button} type="button" onClick={() => setConfirm('shutdown')}>
            Shut down
          </button>
        </>
      )}
      {error && <span className={ui.error}>{error.message}</span>}
    </div>
  )
}

/** "Restarting…" until StreamZH answers again, then a reload at its (possibly new) address. Mounted once in main.tsx. */
export function RestartOverlay() {
  const { phase, target } = useRestartState()

  useEffect(() => {
    if (phase !== 'restarting' || !target) return
    let cancelled = false
    let timer: number | undefined
    const started = Date.now()
    let sawDown = false
    const health = `${target.origin}${target.base}/health`
    const tick = async () => {
      if (cancelled) return
      const up = await reachable(health)
      if (!up) sawDown = true
      // Up again after being down, or after enough time for a restart quicker than the polling.
      if (up && (sawDown || Date.now() - started > 4000)) {
        window.location.href = `${target.origin}${target.base}${target.path}`
        return
      }
      if (Date.now() - started > 90_000) {
        setState({ phase: 'timeout', target })
        return
      }
      timer = window.setTimeout(() => void tick(), 700)
    }
    timer = window.setTimeout(() => void tick(), 800)
    return () => {
      cancelled = true
      window.clearTimeout(timer)
    }
  }, [phase, target])

  if (phase === 'idle') return null
  const address = target ? `${target.origin}${target.base}/` : ''

  return (
    <div className={overlay.overlay} role="alertdialog" aria-live="assertive" aria-label="StreamZH restart">
      <div className={overlay.card}>
        {phase === 'restarting' && (
          <>
            <Loader size="lg" />
            <h3>Restarting StreamZH</h3>
            <p>The page reloads when StreamZH is back, usually after a few seconds.</p>
            {address !== `${window.location.origin}${urlBase}/` && (
              <p>
                New address: <span className={overlay.mono}>{address}</span>
              </p>
            )}
          </>
        )}
        {phase === 'timeout' && (
          <>
            <div className={overlay.icon}>
              <Icon name="alert" size={24} />
            </div>
            <h3>StreamZH did not come back</h3>
            <p>
              It may listen on another address now, or it could not start. Try{' '}
              <a className={overlay.link} href={address}>
                {address}
              </a>
              , or look at the log file in the data folder.
            </p>
          </>
        )}
        {phase === 'stopped' && (
          <>
            <div className={`${overlay.icon} ${overlay.stopped}`}>
              <Icon name="power" size={24} />
            </div>
            <h3>StreamZH has stopped</h3>
            <p>Start it again where it runs: the Windows service, the terminal, or Docker (a container with a restart policy starts again by itself).</p>
          </>
        )}
      </div>
    </div>
  )
}
