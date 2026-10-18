import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router'
import { mediaManagementApi, mediaManagementKeys, type BlocklistFilter } from '../api/mediaManagement'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import Toasts from '../indexerTools/Toast'
import { formatBytes } from '../lib/format'
import { joinParts, relativeTime } from '../status/format'
import StatusIcon from '../status/icons'
import { useIsAdmin } from '../status/useOngoing'
import { mediaTypeOptions, pick } from './filterOptions'
import { FilterBar, FilterSelect, NoMatches, SearchBox } from './Filters'
import { useMediaLinks, useToasts } from './hooks'
import { useClampPage, useListParams } from './listParams'
import Pager from './Pager'
import { usePageSize } from './paging'
import { Empty } from './QueueTab'
import styles from './Activity.module.css'

const filterKeys = ['q', 'type'] as const

/** Releases that failed and are not grabbed again for the same movie or episodes (Radarr/Sonarr "Blocklist"). */
export default function BlocklistTab() {
  const admin = useIsAdmin()
  const queryClient = useQueryClient()
  const { page, get, setFilters, setSearch, setPage, clear: clearFilters, active } = useListParams(filterKeys)
  const [pageSize, setPageSize] = usePageSize('activity-blocklist')
  const type = pick<NonNullable<BlocklistFilter['mediaType']>>(get('type'), mediaTypeOptions)
  const filter: BlocklistFilter = { search: get('q') || undefined, mediaType: type || undefined }
  const list = useQuery({
    queryKey: mediaManagementKeys.blocklist(page, pageSize, filter),
    queryFn: () => mediaManagementApi.blocklist(page, pageSize, filter),
    placeholderData: (previous) => previous,
  })
  const settled = list.data && !list.isPlaceholderData ? list.data.totalRecords : undefined
  useClampPage(page, settled, pageSize, setPage)
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
  const total = list.data.totalRecords
  if (total === 0 && !active) {
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

  return (
    <>
      <div className={styles.filterHead}>
        <FilterBar label="Filter the blocklist" active={active} matches={settled} noun="releases" onClear={clearFilters}>
          <SearchBox value={get('q')} onChange={setSearch} placeholder="Search release titles" label="Search the blocklist" />
          <FilterSelect label="Type" value={type} onChange={(v) => setFilters({ type: v })} options={mediaTypeOptions} />
        </FilterBar>
        {admin && (
          <button type="button" className={ui.button} onClick={() => setConfirmClear(true)}>
            <StatusIcon name="trash" size={14} /> Clear all
          </button>
        )}
      </div>
      {records.length === 0 ? (
        <NoMatches noun="releases" onClear={clearFilters} />
      ) : (
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
      )}
      <Pager page={page} pageSize={pageSize} total={total} noun="blocklisted releases" one="blocklisted release" onPage={setPage} onPageSize={setPageSize} />
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
