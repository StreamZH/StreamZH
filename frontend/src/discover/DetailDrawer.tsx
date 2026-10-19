import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useId, useMemo, useRef, useState } from 'react'
import { Link } from 'react-router'
import { ApiError } from '../api/client'
import { backdropSrc, badgeFor, discoverApi, discoverKeys, isAvailable, posterSrc, type DiscoverDetail, type DiscoverSeason } from '../api/discover'
import { limitText, meKeys, useMe } from '../api/me'
import { isOpen, requestKeys, requestsApi, statusLabels, type MediaRequest } from '../api/requests'
import Icon from '../components/Icon'
import Loader from '../components/Loader'
import ui from '../components/ui.module.css'
import { formatRuntime } from '../lib/format'
import { useRole } from '../requests/roles'
import { PosterImg } from './DiscoverCard'
import styles from './Discover.module.css'
import { useOpenTitle, type OpenTitle } from './openTitle'

/** The detail of the title in ?open=…: a drawer from the right on wide screens, a full page on phones. */
export default function DetailDrawer() {
  const [open, setOpen] = useOpenTitle()
  if (!open) return null
  return <Drawer key={`${open.mediaType}-${open.tmdbId}`} title={open} onClose={() => setOpen(null)} />
}

function Drawer({ title, onClose }: { title: OpenTitle; onClose: () => void }) {
  const ref = useRef<HTMLDialogElement>(null)
  const headingId = useId()
  const detail = useQuery({ queryKey: discoverKeys.detail(title.mediaType, title.tmdbId), queryFn: () => discoverApi.detail(title.mediaType, title.tmdbId) })

  useEffect(() => {
    const dialog = ref.current
    if (dialog && !dialog.open) dialog.showModal()
    return () => dialog?.close()
  }, [])

  return (
    <dialog
      ref={ref}
      className={styles.drawer}
      aria-labelledby={headingId}
      onCancel={(e) => {
        e.preventDefault()
        onClose()
      }}
      // A click on the dimmed page next to the drawer closes it (the drawer itself is filled by .drawerIn).
      onClick={(e) => e.target === ref.current && onClose()}
    >
      <div className={styles.drawerIn}>
        <button type="button" className={styles.drawerClose} onClick={onClose} aria-label="Close">
          <Icon name="x" size={18} />
        </button>
        {detail.isError ? (
          <div className={styles.drawerBody}>
            <h2 id={headingId} className={styles.dTitle}>
              Not available
            </h2>
            <p className={ui.error}>{detail.error.message}</p>
          </div>
        ) : !detail.data ? (
          <div className={styles.drawerLoading}>
            <span id={headingId} hidden>
              Loading
            </span>
            <Loader text="Loading the details…" />
          </div>
        ) : (
          <Detail d={detail.data} headingId={headingId} />
        )}
      </div>
    </dialog>
  )
}

function Detail({ d, headingId }: { d: DiscoverDetail; headingId: string }) {
  const { member } = useRole()
  const backdrop = backdropSrc(d)
  const badge = badgeFor(d.status)
  const facts = [
    d.year ? String(d.year) : null,
    d.runtime ? formatRuntime(d.runtime) : null,
    d.network ?? null,
    (d.genres ?? []).slice(0, 3).join(', ') || null,
  ].filter(Boolean)
  const itemId = d.status.itemId
  const libraryLink = member && itemId ? `/library/${d.mediaType === 'movie' ? 'movies' : 'series'}/${itemId}` : null

  return (
    <>
      <div className={styles.hero} aria-hidden="true">
        {backdrop && <img src={backdrop} alt="" />}
      </div>
      <div className={styles.drawerBody}>
        <div className={styles.dHead}>
          <span className={styles.dPoster}>
            <PosterImg src={posterSrc(d)} title={d.title} />
          </span>
          <div className={styles.dText}>
            <span className={styles.dKind}>
              <Icon name={d.mediaType === 'movie' ? 'film' : 'tv'} size={13} />
              {d.mediaType === 'movie' ? 'Movie' : 'Series'}
            </span>
            <h2 id={headingId} className={styles.dTitle}>
              {d.title}
            </h2>
            {facts.length > 0 && <p className={styles.dFacts}>{facts.join(' · ')}</p>}
            <div className={styles.dBadges}>
              {badge && <span className={`${styles.dBadge} ${styles[`flag_${badge.tone}`]}`}>{badge.label}</span>}
              {d.rating ? (
                <span className={`${ui.pill} ${ui.num}`} title="TMDB rating">
                  <Icon name="star" size={12} />
                  {d.rating.toFixed(1)}
                </span>
              ) : null}
              {libraryLink && (
                <Link to={libraryLink} className={styles.libLink}>
                  Open in library
                  <Icon name="right" size={13} />
                </Link>
              )}
            </div>
          </div>
        </div>

        {d.overview && <p className={styles.overview}>{d.overview}</p>}

        <RequestBox d={d} />
      </div>
    </>
  )
}

/** The state of a season for the picker: what it shows and whether it can still be requested. */
function seasonState(season: DiscoverSeason) {
  const s = season.status ?? {}
  if (isAvailable(s)) return { label: 'Available', tone: 'ok', requestable: false }
  if (s.requested && isOpen(s.requested)) {
    const label = s.requested === 'pending' ? 'Pending' : s.requested === 'partiallyAvailable' ? 'Partially' : 'Requested'
    return { label, tone: s.requested === 'pending' ? 'wait' : s.requested === 'partiallyAvailable' ? 'part' : 'req', requestable: false }
  }
  if (s.partiallyAvailable) return { label: 'Partially', tone: 'part', requestable: true }
  if (s.inLibrary) return { label: 'In library', tone: 'lib', requestable: true }
  return { label: '', tone: '', requestable: true }
}

function RequestBox({ d }: { d: DiscoverDetail }) {
  const queryClient = useQueryClient()
  const { userId } = useRole()
  const me = useMe()
  const noteId = useId()
  const [note, setNote] = useState('')
  const [done, setDone] = useState<MediaRequest | null>(null)
  const series = d.mediaType === 'series'
  const seasons = useMemo(
    () => [...(d.seasons ?? [])].sort((a, b) => (a.seasonNumber === 0 ? 1 : b.seasonNumber === 0 ? -1 : a.seasonNumber - b.seasonNumber)),
    [d.seasons],
  )
  const requestable = seasons.filter((s) => seasonState(s).requestable)
  const [picked, setPicked] = useState<Set<number>>(() => new Set(requestable.filter((s) => s.seasonNumber > 0).map((s) => s.seasonNumber)))

  // The open request for this title: whose it is decides between "requested by you" (with Cancel) and "requested".
  const requestId = d.status.requested && isOpen(d.status.requested) ? d.status.requestId : null
  const existing = useQuery({
    queryKey: requestKeys.one(requestId ?? 0),
    queryFn: () => requestsApi.get(requestId!),
    enabled: requestId != null,
    retry: false,
  })
  const mine = existing.data && existing.data.requestedBy != null && existing.data.requestedBy === userId

  const refresh = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: discoverKeys.all }),
      queryClient.invalidateQueries({ queryKey: requestKeys.all }),
      queryClient.invalidateQueries({ queryKey: meKeys.me }),
    ])

  const create = useMutation({
    mutationFn: () => {
      const all = requestable.length === seasons.length && seasons.filter((s) => s.seasonNumber > 0).every((s) => picked.has(s.seasonNumber))
      return requestsApi.create({
        mediaType: d.mediaType,
        tmdbId: d.tmdbId,
        ...(series ? { seasons: seasons.length === 0 || all ? ('all' as const) : [...picked].sort((a, b) => a - b) } : {}),
        ...(note.trim() ? { note: note.trim() } : {}),
      })
    },
    onSuccess: (r) => {
      setDone(r)
      void refresh()
    },
    onError: (e) => {
      if (e instanceof ApiError && e.status === 409) void refresh()
    },
  })
  const cancel = useMutation({ mutationFn: () => requestsApi.remove(requestId!), onSuccess: () => void refresh() })

  const error =
    create.error instanceof ApiError && create.error.status === 409
      ? 'Someone requested it in the meantime, or it is already in the library. The state above is up to date.'
      : create.error instanceof ApiError
        ? (create.error.fieldErrors.limit?.[0] ?? create.error.fieldErrors.seasons?.[0] ?? create.error.fieldErrors.note?.[0] ?? create.error.message)
        : create.error
          ? String(create.error)
          : null
  const limit = limitText(series ? me.data?.usage?.series : me.data?.usage?.movies, series ? 'series' : 'movies')
  const canRequest = me.data?.permissions ? me.data.permissions.canRequest : true

  if (done) {
    const approved = done.status !== 'pending' && done.status !== 'declined'
    return (
      <div className={`${ui.infoBox} ${styles.state} ${styles.stateOk}`} role="status">
        <Icon name="check" size={17} />
        <div>
          <b>{approved ? 'Requested and approved' : 'Requested'}</b>
          <p>
            {approved
              ? 'StreamZH adds it to the library and looks for it. You can follow it under Requests.'
              : 'An admin has to approve it first. You can follow it under Requests.'}
          </p>
          <Link to="/requests?tab=mine" className={styles.libLink}>
            Your requests
            <Icon name="right" size={13} />
          </Link>
        </div>
      </div>
    )
  }

  const available = isAvailable(d.status)
  const movieBlocked = !series && (available || d.status.inLibrary || requestId != null)
  const seriesBlocked = series && (available || (seasons.length > 0 && requestable.length === 0) || (seasons.length === 0 && requestId != null))

  return (
    <div className={styles.requestBox}>
      {requestId != null && (
        <div className={`${ui.infoBox} ${styles.state}`}>
          <Icon name={d.status.requested === 'pending' ? 'clock' : 'check'} size={17} />
          <div>
            <b>
              {mine ? 'Already requested by you' : 'Already requested'} · {statusLabels[d.status.requested!].toLowerCase()}
            </b>
            {existing.data?.seasons && existing.data.seasons.length > 0 && series && (
              <p>Seasons {existing.data.seasons.map((s) => (s === 0 ? 'Specials' : s)).join(', ')}</p>
            )}
            {mine && d.status.requested === 'pending' && (
              <button type="button" className={`${ui.ghost} ${ui.sm} ${styles.cancel}`} onClick={() => cancel.mutate()} disabled={cancel.isPending}>
                <Icon name="x" size={14} />
                {cancel.isPending ? 'Cancelling…' : 'Cancel request'}
              </button>
            )}
            {cancel.error && <p className={ui.error}>{cancel.error.message}</p>}
          </div>
        </div>
      )}

      {available && (
        <div className={`${ui.infoBox} ${styles.state} ${styles.stateOk}`}>
          <Icon name="check" size={17} />
          <div>
            <b>Available</b>
            <p>{series ? 'Every aired episode is in the library.' : 'It is in the library, ready to watch.'}</p>
          </div>
        </div>
      )}
      {!series && !available && d.status.inLibrary && requestId == null && (
        <div className={`${ui.infoBox} ${styles.state}`}>
          <Icon name="clock" size={17} />
          <div>
            <b>In the library</b>
            <p>StreamZH is looking for it; no request needed.</p>
          </div>
        </div>
      )}

      {series && seasons.length > 0 && (
        <fieldset className={styles.seasons} disabled={!canRequest}>
          <legend className={styles.seasonsHead}>
            <span>Seasons</span>
            {requestable.length > 1 && (
              <button
                type="button"
                className={`${ui.ghost} ${ui.sm}`}
                onClick={() =>
                  setPicked((p) =>
                    p.size === requestable.length ? new Set() : new Set(requestable.map((s) => s.seasonNumber)),
                  )
                }
              >
                {picked.size === requestable.length ? 'Select none' : 'Select all'}
              </button>
            )}
          </legend>
          <ul className={styles.seasonList}>
            {seasons.map((s) => {
              const state = seasonState(s)
              const checked = state.requestable ? picked.has(s.seasonNumber) : state.tone === 'ok' || state.tone === 'wait' || state.tone === 'req' || state.tone === 'part'
              const name = s.seasonNumber === 0 ? 'Specials' : s.name && !/^season \d+$/i.test(s.name) ? `Season ${s.seasonNumber} · ${s.name}` : `Season ${s.seasonNumber}`
              const meta = [s.episodeCount ? `${s.episodeCount} episode${s.episodeCount === 1 ? '' : 's'}` : null, s.airDate ? s.airDate.slice(0, 4) : null]
                .filter(Boolean)
                .join(' · ')
              return (
                <li key={s.seasonNumber}>
                  <label className={`${styles.season} ${state.requestable ? '' : styles.seasonDone}`}>
                    <input
                      type="checkbox"
                      className={styles.checkbox}
                      checked={checked}
                      disabled={!state.requestable}
                      onChange={(e) =>
                        setPicked((p) => {
                          const next = new Set(p)
                          if (e.target.checked) next.add(s.seasonNumber)
                          else next.delete(s.seasonNumber)
                          return next
                        })
                      }
                    />
                    <span className={styles.seasonText}>
                      <b>{name}</b>
                      {meta && <small>{meta}</small>}
                    </span>
                    {state.label && <span className={`${styles.dBadge} ${styles[`flag_${state.tone}`]}`}>{state.label}</span>}
                  </label>
                </li>
              )
            })}
          </ul>
        </fieldset>
      )}

      {!canRequest ? (
        <p className={ui.muted}>Your account cannot request titles. Ask an admin of this StreamZH.</p>
      ) : (
        !movieBlocked &&
        !seriesBlocked && (
          <div className={styles.form}>
            <label className={styles.noteLabel} htmlFor={noteId}>
              Note <span className={ui.muted}>(optional)</span>
            </label>
            <textarea
              id={noteId}
              className={`${ui.input} ${styles.note}`}
              rows={2}
              maxLength={500}
              placeholder="Anything the admins should know, like the language you want"
              value={note}
              onChange={(e) => setNote(e.target.value)}
            />
            {error && (
              <div className={ui.errBox} role="alert">
                <Icon name="alert" size={15} />
                {error}
              </div>
            )}
            <div className={styles.formActions}>
              <button
                type="button"
                className={ui.primary}
                onClick={() => create.mutate()}
                disabled={create.isPending || (series && seasons.length > 0 && picked.size === 0)}
              >
                <Icon name="plus" size={15} />
                {create.isPending
                  ? 'Requesting…'
                  : series && seasons.length > 0
                    ? `Request ${picked.size === 1 ? '1 season' : `${picked.size} seasons`}`
                    : 'Request'}
              </button>
              {limit && <span className={styles.limitLine}>{limit}</span>}
            </div>
          </div>
        )
      )}
    </div>
  )
}
