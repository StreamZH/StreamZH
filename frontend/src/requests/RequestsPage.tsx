import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useSearchParams } from 'react-router'
import { ApiError } from '../api/client'
import { discoverKeys } from '../api/discover'
import {
  isOpen,
  requesterName,
  requestKeys,
  requestsApi,
  seasonsText,
  statusLabels,
  type ApproveOptions,
  type MediaRequest,
  type RequestFilter,
  type RequestMediaType,
  type RequestStatus,
} from '../api/requests'
import Pager from '../activity/Pager'
import { usePageSize } from '../activity/paging'
import Icon, { type IconName } from '../components/Icon'
import { Skeleton } from '../components/Loader'
import PageHeader from '../components/PageHeader'
import ui from '../components/ui.module.css'
import { formatDateTime, relativeTime, tmdbImage } from '../lib/format'
import { useAdvanced } from '../lib/advancedMode'
import { useAutomation } from '../migration/hooks'
import { PosterImg } from '../discover/DiscoverCard'
import { ApproveDialog, DeclineDialog, RemoveRequestDialog } from './RequestDialogs'
import { useRole } from './roles'
import StatusChip from './StatusChip'
import { usePendingCount } from './usePendingCount'
import styles from './Requests.module.css'

type Tab = 'pending' | 'all' | 'mine'

const statuses: RequestStatus[] = ['pending', 'approved', 'processing', 'partiallyAvailable', 'available', 'declined', 'failed']

const sources: Record<MediaRequest['source'], { icon: IconName; label: string }> = {
  web: { icon: 'globe', label: 'Web' },
  discord: { icon: 'chat', label: 'Discord' },
  api: { icon: 'code', label: 'API' },
}

const errorText = (e: unknown) => (e instanceof ApiError ? (Object.values(e.fieldErrors)[0]?.[0] ?? e.message) : e ? String(e) : null)

/** Requests: pending ones for admins to approve or decline, all requests, and your own (requesters see only theirs). */
export default function RequestsPage() {
  const { admin } = useRole()
  const [params, setParams] = useSearchParams()
  const pending = usePendingCount()
  const automation = useAutomation()
  const tabParam = params.get('tab') as Tab | null
  const tab: Tab = admin ? (tabParam === 'all' || tabParam === 'mine' ? tabParam : 'pending') : 'mine'
  const type = (['movie', 'series'].includes(params.get('type') ?? '') ? params.get('type') : null) as RequestMediaType | null
  const status = (statuses.includes(params.get('status') as RequestStatus) ? params.get('status') : null) as RequestStatus | null
  const page = Math.max(1, Number(params.get('page')) || 1)
  const [pageSize, setPageSize] = usePageSize('requests')

  const set = (change: Record<string, string | null>) => {
    const next = new URLSearchParams(params)
    for (const [key, value] of Object.entries(change)) {
      if (value == null) next.delete(key)
      else next.set(key, value)
    }
    if (!('page' in change)) next.delete('page')
    setParams(next, { replace: true })
  }

  const filter: RequestFilter = {
    status: tab === 'pending' ? 'pending' : (status ?? undefined),
    mediaType: type ?? undefined,
    mine: tab === 'mine' || undefined,
    page,
    pageSize,
  }
  const list = useQuery({ queryKey: requestKeys.list(filter), queryFn: () => requestsApi.list(filter), refetchInterval: 60_000, placeholderData: (prev) => prev })
  const records = list.data?.records ?? []
  const total = list.data?.totalRecords ?? 0

  const lead = admin
    ? pending > 0
      ? `${pending} ${pending === 1 ? 'request waits' : 'requests wait'} for your approval.`
      : 'Nothing waits for your approval.'
    : 'What you asked for and where it stands.'

  return (
    <div className={styles.page}>
      <PageHeader
        title={admin ? 'Requests' : 'Your requests'}
        lead={lead}
        actions={
          <Link to="/discover" className={ui.button}>
            <Icon name="compass" size={15} />
            Discover
          </Link>
        }
      />

      {admin && automation.data?.hold && (
        <div className={`${ui.infoBox} ${styles.hold}`}>
          <Icon name="pause" size={15} />
          <span>Side by side is on: approved requests are added and monitored, but StreamZH grabs nothing until you switch over.</span>
        </div>
      )}

      {admin && (
        <div className={styles.tabs} role="tablist" aria-label="Requests">
          {(
            [
              ['pending', 'Pending', 'clock'],
              ['all', 'All', 'inbox'],
              ['mine', 'Mine', 'user'],
            ] as const
          ).map(([key, text, icon]) => (
            <button key={key} type="button" role="tab" aria-selected={tab === key} className={styles.tab} onClick={() => set({ tab: key, status: null })}>
              <Icon name={icon} size={15} />
              {text}
              {key === 'pending' && pending > 0 && <span className={`${styles.tcount} ${styles.tcountHot}`}>{pending}</span>}
            </button>
          ))}
        </div>
      )}

      <div className={styles.toolbar}>
        <div className={ui.seg} role="group" aria-label="Type">
          {(
            [
              [null, 'All'],
              ['movie', 'Movies'],
              ['series', 'Series'],
            ] as const
          ).map(([value, text]) => (
            <button key={text} type="button" aria-pressed={type === value} onClick={() => set({ type: value })}>
              {value && <Icon name={value === 'movie' ? 'film' : 'tv'} size={15} />}
              {text}
            </button>
          ))}
        </div>
        {tab !== 'pending' && (
          <select className={`${ui.select} ${styles.statusSelect}`} aria-label="Status" value={status ?? ''} onChange={(e) => set({ status: e.target.value || null })}>
            <option value="">Every status</option>
            {statuses.map((s) => (
              <option key={s} value={s}>
                {statusLabels[s]}
              </option>
            ))}
          </select>
        )}
        {list.isFetching && !list.isPending && <span className={ui.dotBusy} aria-label="Refreshing" />}
      </div>

      {list.isError ? (
        <p className={ui.error}>{list.error.message}</p>
      ) : list.isPending ? (
        <div className={ui.panel}>
          <Skeleton rows={5} />
        </div>
      ) : records.length === 0 ? (
        <Empty tab={tab} filtered={!!(type || status)} />
      ) : (
        <>
          <div className={`${ui.panel} ${styles.tablePanel}`}>
            <table className={`${ui.table} ${styles.table}`}>
              <thead>
                <tr>
                  <th>Title</th>
                  <th>{tab === 'mine' ? 'Requested' : 'Requested by'}</th>
                  <th>Status</th>
                  <th className={styles.actionsHead}>
                    <span className={styles.srOnly}>Actions</span>
                  </th>
                </tr>
              </thead>
              <tbody>
                {records.map((r) => (
                  <RequestRow key={r.id} r={r} layout="row" mine={tab === 'mine'} />
                ))}
              </tbody>
            </table>
          </div>
          <ul className={styles.cards}>
            {records.map((r) => (
              <RequestRow key={r.id} r={r} layout="card" mine={tab === 'mine'} />
            ))}
          </ul>
          <Pager page={page} pageSize={pageSize} total={total} noun="requests" onPage={(p) => set({ page: String(p) })} onPageSize={setPageSize} />
        </>
      )}
    </div>
  )
}

function Empty({ tab, filtered }: { tab: Tab; filtered: boolean }) {
  return (
    <div className={`${ui.panel} ${ui.empty}`}>
      <Icon name={tab === 'pending' ? 'check' : 'inbox'} size={24} />
      <h3>{filtered ? 'No matching requests' : tab === 'pending' ? 'All caught up' : 'No requests yet'}</h3>
      <p>
        {filtered
          ? 'Try another type or status.'
          : tab === 'pending'
            ? 'New requests show up here for you to approve or decline.'
            : 'Find something on Discover and request it.'}
      </p>
      {!filtered && tab !== 'pending' && (
        <Link to="/discover" className={ui.primary}>
          <Icon name="compass" size={15} />
          Discover
        </Link>
      )}
    </div>
  )
}

/** Where the title links to: the library item for members once it was added, otherwise its Discover detail. */
function titleLink(r: MediaRequest, member: boolean): string {
  if (member && r.itemId) return `/library/${r.mediaType === 'movie' ? 'movies' : 'series'}/${r.itemId}`
  return `/discover?open=${r.mediaType}-${r.tmdbId}`
}

/** One request, as a table row (desktop) or a card (phone), with its actions and dialogs. */
function RequestRow({ r, layout, mine }: { r: MediaRequest; layout: 'row' | 'card'; mine: boolean }) {
  const { admin, member, userId } = useRole()
  const advanced = useAdvanced()
  const queryClient = useQueryClient()
  const [dialog, setDialog] = useState<'approve' | 'decline' | 'remove' | null>(null)
  const own = r.requestedBy != null && r.requestedBy === userId

  const done = () => {
    setDialog(null)
    void queryClient.invalidateQueries({ queryKey: requestKeys.all })
    void queryClient.invalidateQueries({ queryKey: discoverKeys.all })
  }
  const approve = useMutation({ mutationFn: (options: ApproveOptions) => requestsApi.approve(r.id, options), onSuccess: done })
  const decline = useMutation({ mutationFn: (reason: string | null) => requestsApi.decline(r.id, reason), onSuccess: done })
  const remove = useMutation({ mutationFn: () => requestsApi.remove(r.id), onSuccess: done })
  const retry = useMutation({ mutationFn: () => requestsApi.retry(r.id), onSuccess: done })

  const kind = r.mediaType === 'movie' ? 'Movie' : 'Series'
  const seasons = seasonsText(r.mediaType, r.seasons)
  const meta = [r.year ? String(r.year) : null, kind, seasons || null].filter(Boolean).join(' · ')
  const source = sources[r.source] ?? sources.web
  const poster = r.posterUrl || tmdbImage(r.posterPath, 'w92')
  const rowError = errorText(dialog ? null : (approve.error ?? retry.error))

  const canCancel = own && r.status === 'pending'
  const actions = (
    <div className={styles.actions}>
      {admin && r.status === 'pending' && (
        <>
          <button type="button" className={`${ui.primary} ${ui.sm}`} onClick={() => approve.mutate({})} disabled={approve.isPending}>
            <Icon name="check" size={14} />
            {approve.isPending ? 'Approving…' : 'Approve'}
          </button>
          {advanced && (
            <button
              type="button"
              className={`${ui.button} ${ui.sm} ${ui.icon}`}
              onClick={() => setDialog('approve')}
              title="Approve with another profile or folder"
              aria-label={`Approve ${r.title} with options`}
            >
              <Icon name="sliders" size={14} />
            </button>
          )}
          <button type="button" className={`${ui.button} ${ui.sm}`} onClick={() => setDialog('decline')}>
            <Icon name="ban" size={14} />
            Decline
          </button>
        </>
      )}
      {admin && r.status === 'failed' && (
        <button type="button" className={`${ui.button} ${ui.sm}`} onClick={() => retry.mutate()} disabled={retry.isPending}>
          <Icon name="restart" size={14} />
          {retry.isPending ? 'Retrying…' : 'Retry'}
        </button>
      )}
      {canCancel && !admin && (
        <button type="button" className={`${ui.button} ${ui.sm}`} onClick={() => setDialog('remove')}>
          <Icon name="x" size={14} />
          Cancel
        </button>
      )}
      {admin && (canCancel || !isOpen(r.status)) && (
        <button
          type="button"
          className={`${ui.ghost} ${ui.sm} ${ui.icon}`}
          onClick={() => setDialog('remove')}
          title={canCancel ? 'Cancel your request' : 'Remove the request'}
          aria-label={`${canCancel ? 'Cancel' : 'Remove'} the request for ${r.title}`}
        >
          <Icon name={canCancel ? 'x' : 'trash'} size={14} />
        </button>
      )}
    </div>
  )

  const titleCell = (
    <div className={styles.titleCell}>
      <span className={styles.thumb}>
        <PosterImg src={poster} title={r.title} />
      </span>
      <div className={styles.titleText}>
        <Link to={titleLink(r, member)} className={styles.title}>
          {r.title}
        </Link>
        <span className={styles.meta}>{meta}</span>
        {r.note && <span className={styles.note}>“{r.note}”</span>}
      </div>
    </div>
  )

  // In "Mine" every request is the user's own: only when and from where.
  const who = mine ? (
    <span className={styles.when} title={formatDateTime(r.created)}>
      {relativeTime(r.created)}
      <span className={styles.source} title={`Requested via ${source.label}`}>
        <Icon name={source.icon} size={12} />
        {source.label}
      </span>
    </span>
  ) : (
    <div className={styles.who}>
      <span className={styles.avatar} aria-hidden="true">
        {requesterName(r).slice(0, 1).toUpperCase()}
      </span>
      <div className={styles.whoText}>
        <b>
          {requesterName(r)}
          {own && <span className={styles.you}> (you)</span>}
        </b>
        <span className={styles.when} title={formatDateTime(r.created)}>
          {relativeTime(r.created)}
          <span className={styles.source} title={`Requested via ${source.label}`}>
            <Icon name={source.icon} size={12} />
            {source.label}
          </span>
        </span>
      </div>
    </div>
  )

  const state = (
    <div className={styles.state}>
      <StatusChip status={r.status} title={r.decided ? `Decided ${formatDateTime(r.decided)}${r.decidedByName ? ` by ${r.decidedByName}` : ''}` : undefined} />
      {r.reason && (r.status === 'declined' || r.status === 'failed') && <span className={styles.reason}>{r.reason}</span>}
      {rowError && (
        <span className={ui.error} role="alert">
          {rowError}
        </span>
      )}
    </div>
  )

  const dialogs = (
    <>
      {dialog === 'approve' && (
        <ApproveDialog request={r} pending={approve.isPending} error={errorText(approve.error)} onApprove={(o) => approve.mutate(o)} onClose={() => setDialog(null)} />
      )}
      {dialog === 'decline' && (
        <DeclineDialog request={r} pending={decline.isPending} error={errorText(decline.error)} onDecline={(reason) => decline.mutate(reason)} onClose={() => setDialog(null)} />
      )}
      {dialog === 'remove' && (
        <RemoveRequestDialog request={r} own={own} pending={remove.isPending} error={errorText(remove.error)} onConfirm={() => remove.mutate()} onClose={() => setDialog(null)} />
      )}
    </>
  )

  if (layout === 'card') {
    return (
      <li className={styles.card}>
        {titleCell}
        <div className={styles.cardRow}>
          {who}
          {state}
        </div>
        {actions}
        {dialogs}
      </li>
    )
  }

  return (
    <tr>
      <td>{titleCell}</td>
      <td>{who}</td>
      <td>{state}</td>
      <td className={styles.actionsCell}>
        {actions}
        {dialogs}
      </td>
    </tr>
  )
}
