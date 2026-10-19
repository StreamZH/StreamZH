import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useRef, useState } from 'react'
import { apiUrl } from '../api/client'
import { calendarApi, calendarKeys } from '../api/wanted'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import styles from './Calendar.module.css'

/**
 * Subscribe in Google Calendar, Outlook or Apple Calendar, like Radarr's "iCal link". Since 0.12 the link carries a
 * read-only feed token instead of the API key: it opens this feed and nothing else.
 */
export default function IcalDialog({ onClose }: { onClose: () => void }) {
  const queryClient = useQueryClient()
  const general = useQuery({ queryKey: calendarKeys.feedToken, queryFn: calendarApi.feedToken })
  const [confirming, setConfirming] = useState(false)
  const renew = useMutation({
    mutationFn: calendarApi.newFeedToken,
    onSuccess: (data) => {
      queryClient.setQueryData(calendarKeys.feedToken, data)
      setConfirming(false)
      setCopied(null)
    },
  })
  const [pastDays, setPastDays] = useState(7)
  const [futureDays, setFutureDays] = useState(28)
  const [unmonitored, setUnmonitored] = useState(false)
  const [movies, setMovies] = useState(true)
  const [series, setSeries] = useState(true)
  const [copied, setCopied] = useState<'ok' | 'failed' | null>(null)
  const input = useRef<HTMLInputElement>(null)

  const params = new URLSearchParams({ token: general.data?.token ?? '' })
  if (pastDays !== 7) params.set('pastDays', String(pastDays))
  if (futureDays !== 28) params.set('futureDays', String(futureDays))
  if (unmonitored) params.set('unmonitored', 'true')
  if (movies !== series) params.set('tags', movies ? 'movies' : 'series')
  const feed = `${window.location.origin}${apiUrl('/calendar/feed.ics')}?${params}`
  const webcal = feed.replace(/^https?:/, 'webcal:')

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(feed)
      setCopied('ok')
    } catch {
      // Without HTTPS the clipboard API is missing: select the text so Ctrl+C works.
      input.current?.select()
      setCopied('failed')
    }
  }

  const days = (value: string, max: number) => Math.max(0, Math.min(max, Math.round(Number(value) || 0)))

  return (
    <Modal title="iCal feed" onClose={onClose}>
      <p className={styles.dialogText}>
        Calendar apps can subscribe to this address and show your movies and episodes. The link opens only this calendar; anyone who has it can read it.
      </p>
      {general.isError ? (
        <p className={ui.error}>{general.error.message}</p>
      ) : (
        <div className={styles.icalForm}>
          <div className={styles.icalRow}>
            <label className={ui.field}>
              Past days
              <input className={ui.input} type="number" min={0} max={365} value={pastDays} onChange={(e) => setPastDays(days(e.target.value, 365))} />
            </label>
            <label className={ui.field}>
              Future days
              <input className={ui.input} type="number" min={0} max={365} value={futureDays} onChange={(e) => setFutureDays(days(e.target.value, 365))} />
            </label>
          </div>
          <div className={styles.icalChecks}>
            <label className={ui.check}>
              <input type="checkbox" checked={movies} onChange={(e) => setMovies(e.target.checked || !series)} />
              Movies
            </label>
            <label className={ui.check}>
              <input type="checkbox" checked={series} onChange={(e) => setSeries(e.target.checked || !movies)} />
              Series
            </label>
            <label className={ui.check}>
              <input type="checkbox" checked={unmonitored} onChange={(e) => setUnmonitored(e.target.checked)} />
              Include unmonitored
            </label>
          </div>
          <label className={ui.field}>
            Feed address
            <input ref={input} className={`${ui.input} ${ui.mono}`} readOnly value={general.data ? feed : 'Loading…'} onFocus={(e) => e.target.select()} />
            {copied === 'ok' && <small>Copied.</small>}
            {copied === 'failed' && <small>Copying is blocked by the browser here: the address is selected, press Ctrl+C.</small>}
          </label>
          <div className={styles.icalRenew}>
            {confirming ? (
              <>
                <span>Calendar apps with the old link stop updating.</span>
                <button type="button" className={`${ui.danger} ${ui.sm}`} onClick={() => renew.mutate()} disabled={renew.isPending}>
                  Yes, new link
                </button>
                <button type="button" className={`${ui.button} ${ui.sm}`} onClick={() => setConfirming(false)}>
                  Cancel
                </button>
              </>
            ) : (
              <button type="button" className={`${ui.ghost} ${ui.sm}`} onClick={() => setConfirming(true)} disabled={!general.data}>
                Create new link
              </button>
            )}
            {renew.error && <span className={ui.error}>{renew.error.message}</span>}
          </div>
        </div>
      )}
      <div className={styles.dialogActions}>
        <button type="button" className={ui.button} onClick={onClose}>
          Close
        </button>
        <a className={ui.button} href={webcal} aria-disabled={!general.data}>
          Open in calendar app
        </a>
        <button type="button" className={ui.primary} onClick={() => void copy()} disabled={!general.data}>
          Copy iCal link
        </button>
      </div>
    </Modal>
  )
}
