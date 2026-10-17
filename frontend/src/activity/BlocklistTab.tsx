import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useSearchParams } from 'react-router'
import { mediaManagementApi, mediaManagementKeys } from '../api/mediaManagement'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import Toasts from '../indexerTools/Toast'
import { formatBytes } from '../lib/format'
import { joinParts, relativeTime } from '../status/format'
import StatusIcon from '../status/icons'
import { useIsAdmin } from '../status/useOngoing'
import { useMediaLinks, useToasts } from './hooks'
import { Empty } from './QueueTab'
import styles from './Activity.module.css'

const pageSize = 25

/** Releases that failed and are not grabbed again for the same movie or episodes (Radarr/Sonarr "Blocklist"). */
export default function BlocklistTab() {
  const admin = useIsAdmin()
  const queryClient = useQueryClient()
  const [url, setUrl] = useSearchParams()
  const page = Math.max(1, Number(url.get('page')) || 1)
  const list = useQuery({
    queryKey: mediaManagementKeys.blocklist(page, pageSize),
    queryFn: () => mediaManagementApi.blocklist(page, pageSize),
    placeholderData: (previous) => previous,
  })
  const records = list.data?.records ?? []
  const media = useMediaLinks(records)
  const { toasts, push, dismiss } = useToasts()
  const [confirmClear, setConfirmClear] = useState(false)

  const refresh = () => queryClient.invalidateQueries({ queryKey: mediaManagementKeys.allBlocklist })
  const remove = useMutation({
    mutationFn: mediaManagementApi.unblock,
    onSuccess: () => push('ok', 'Removed from the blocklist', 'The release can be grabbed again.'),
    onError: (e) => push('error', 'Could not remove', e.message),
    onSettled: refresh,
  })
  const clear = useMutation({
    mutationFn: mediaManagementApi.clearBlocklist,
    onSuccess: (r) => {
      setConfirmClear(false)
      push('ok', 'Blocklist cleared', `${r.removed} release${r.removed === 1 ? '' : 's'} removed`)
    },
    onError: (e) => push('error', 'Could not clear the blocklist', e.message),
    onSettled: refresh,
  })

  const onPage = (p: number) => {
    const next = new URLSearchParams(url)
    if (p > 1) next.set('page', String(p))
    else next.delete('page')
    setUrl(next)
  }

  if (list.isError) {
    return (
      <Empty icon="error" title="The blocklist could not be loaded">
        <p>{list.error.message}</p>
      </Empty>
    )
  }
  if (!list.data) {
    return (
      <div className={styles.loading} role="status">
        <span className={styles.bigSpinner} aria-hidden="true" />
        Loading the blocklist…
      </div>
    )
  }
  if (records.length === 0) {
    return (
      <Empty icon="check" title="The blocklist is empty">
        <p>When a download of a movie or episode fails, its release lands here and is not grabbed again. StreamZH searches for another one.</p>
        <div className={styles.emptyActions}>
          <Link className={ui.button} to="/settings/media-management#download-handling">
            Failed download handling
          </Link>
        </div>
      </Empty>
    )
  }

  const pages = Math.max(1, Math.ceil(list.data.totalRecords / list.data.pageSize))
  return (
    <>
      <div className={styles.listHead}>
        <span className={styles.pagerText}>
          {new Intl.NumberFormat().format(list.data.totalRecords)} blocklisted release{list.data.totalRecords === 1 ? '' : 's'}
        </span>
        {admin && (
          <button type="button" className={ui.button} onClick={() => setConfirmClear(true)}>
            <StatusIcon name="trash" size={14} /> Clear all
          </button>
        )}
      </div>
      <ul className={`${styles.historyList} ${list.isPlaceholderData ? styles.stale : ''}`} aria-label="Blocklist">
        {records.map((item) => {
          const link = media(item)
          return (
            <li key={item.id} className={`${styles.historyItem} ${styles.historyFailed}`}>
              <span className={styles.historyIcon} title="Blocklisted">
                <StatusIcon name="x" size={16} />
              </span>
              <div className={styles.historyMain}>
                <span className={styles.dlTitle}>{item.sourceTitle}</span>
                <span className={styles.meta}>
                  {item.quality !== 'Unknown' && <span className={styles.quality}>{item.quality}</span>}
                  {link && (
                    <Link className={styles.mediaLink} to={link.to}>
                      {link.label}
                    </Link>
                  )}
                  <span>{joinParts(item.size > 0 ? formatBytes(item.size) : null, item.indexer, item.languages.join(', '), `blocked ${relativeTime(item.date)}`)}</span>
                </span>
                {item.message && <span className={styles.historyError}>{item.message}</span>}
              </div>
              {admin && (
                <div className={styles.actions}>
                  <button
                    type="button"
                    className={`${styles.iconAction} ${styles.iconDanger}`}
                    onClick={() => remove.mutate(item.id)}
                    disabled={remove.isPending}
                    title="Remove from the blocklist"
                    aria-label={`Remove ${item.sourceTitle} from the blocklist`}
                  >
                    <StatusIcon name="trash" />
                  </button>
                </div>
              )}
            </li>
          )
        })}
      </ul>
      {list.data.totalRecords > list.data.pageSize && (
        <nav className={styles.pager} aria-label="Pages">
          <button className={ui.button} type="button" disabled={page <= 1} onClick={() => onPage(page - 1)}>
            Previous
          </button>
          <span className={styles.pagerText}>
            Page <strong>{page}</strong> of {pages}
          </span>
          <button className={ui.button} type="button" disabled={page >= pages} onClick={() => onPage(page + 1)}>
            Next
          </button>
        </nav>
      )}
      {confirmClear && (
        <Modal title="Clear the blocklist" onClose={() => setConfirmClear(false)}>
          <p className={styles.dialogText}>All blocklisted releases can be grabbed again, also by automatic searches.</p>
          <div className={styles.dialogActions}>
            <button type="button" className={ui.button} onClick={() => setConfirmClear(false)}>
              Cancel
            </button>
            <button type="button" className={ui.danger} onClick={() => clear.mutate()} disabled={clear.isPending}>
              {clear.isPending ? 'Clearing…' : 'Clear all'}
            </button>
          </div>
        </Modal>
      )}
      <Toasts toasts={toasts} onDismiss={dismiss} />
    </>
  )
}
