import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router'
import { mediaManagementApi, mediaManagementKeys, type BlocklistFilter } from '../api/mediaManagement'
import Icon from '../components/Icon'
import Loader from '../components/Loader'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import Toasts from '../indexerTools/Toast'
import { formatBytes } from '../lib/format'
import { joinParts, relativeTime } from '../status/format'
import { useIsAdmin } from '../status/useOngoing'
import { mediaTypeOptions, pick } from './filterOptions'
import { FilterBar, FilterSeg, NoMatches, SearchBox } from './Filters'
import { useMediaLinks, useToasts } from './hooks'
import { useClampPage, useListParams } from './listParams'
import Pager from './Pager'
import { usePageSize } from './paging'
import { Empty } from './Empty'
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
  if (!list.data) return <Loader text="Loading the blocklist" />
  const total = list.data.totalRecords
  if (total === 0 && !active) {
    return (
      <Empty icon="check" title="The blocklist is empty">
        <p>When a download of a movie or episode fails, its release lands here and is not grabbed again. StreamZH searches for another one.</p>
        <div className={styles.emptyActions}>
          <Link className={`${ui.button} ${ui.sm}`} to="/settings/media-management#download-handling">
            Failed download handling
          </Link>
        </div>
      </Empty>
    )
  }

  return (
    <>
      <FilterBar label="Filter the blocklist" active={active} matches={settled} noun="releases" onClear={clearFilters}>
        <FilterSeg label="Type" all="All" value={type} onChange={(v) => setFilters({ type: v })} options={mediaTypeOptions} />
        <SearchBox value={get('q')} onChange={setSearch} placeholder="Search release titles" label="Search the blocklist" />
        {admin && (
          <button type="button" className={`${ui.danger} ${ui.sm} ${styles.toolbarEnd}`} onClick={() => setConfirmClear(true)}>
            <Icon name="trash" size={14} /> Clear all
          </button>
        )}
      </FilterBar>
      {records.length === 0 ? (
        <NoMatches noun="releases" onClear={clearFilters} />
      ) : (
        <div className={`${ui.panel} ${styles.tablePanel} ${list.isPlaceholderData ? styles.stale : ''}`}>
          <table className={`${ui.table} ${styles.table}`} aria-label="Blocklist">
            <thead>
              <tr>
                <th>Release</th>
                <th className={styles.hideSm}>Quality</th>
                <th className={`${styles.r} ${styles.hideSm}`}>Size</th>
                <th className={styles.hideMd}>Indexer</th>
                <th className={styles.hideSm}>Blocked</th>
                {admin && (
                  <th>
                    <span className={styles.srOnly}>Actions</span>
                  </th>
                )}
              </tr>
            </thead>
            <tbody>
              {records.map((item) => {
                const link = media(item)
                const quality = item.quality !== 'Unknown' ? item.quality : null
                const when = relativeTime(item.date)
                return (
                  <tr key={item.id}>
                    <td className={styles.release}>
                      <span className={styles.relTitle} title={item.sourceTitle}>
                        <Icon name="ban" size={14} className={styles.banIcon} />
                        {item.sourceTitle}
                      </span>
                      <span className={styles.sub}>
                        {link && (
                          <Link className={styles.mediaLink} to={link.to}>
                            {link.label}
                          </Link>
                        )}
                        {item.languages.length > 0 && <span>{item.languages.join(', ')}</span>}
                        <span className={styles.showSm}>{joinParts(quality, item.size > 0 ? formatBytes(item.size) : null, `blocked ${when}`)}</span>
                      </span>
                      {item.message && <span className={styles.historyError}>{item.message}</span>}
                    </td>
                    <td className={styles.hideSm}>{quality ? <span className={ui.tag}>{quality}</span> : <span className={styles.dim}>–</span>}</td>
                    <td className={`${styles.r} ${ui.num} ${styles.hideSm} ${styles.nowrap}`}>{item.size > 0 ? formatBytes(item.size) : '–'}</td>
                    <td className={`${styles.hideMd} ${styles.dim}`}>{item.indexer || '–'}</td>
                    <td className={`${styles.hideSm} ${styles.dim} ${styles.nowrap}`}>{when}</td>
                    {admin && (
                      <td className={styles.actionsCell}>
                        <div className={styles.actions}>
                          <button
                            type="button"
                            className={`${ui.ghost} ${ui.icon} ${ui.sm} ${styles.dangerIcon}`}
                            onClick={() => remove.mutate(item.id)}
                            disabled={remove.isPending}
                            title="Remove from the blocklist"
                            aria-label={`Remove ${item.sourceTitle} from the blocklist`}
                          >
                            <Icon name="trash" size={16} />
                          </button>
                        </div>
                      </td>
                    )}
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
      <Pager page={page} pageSize={pageSize} total={total} noun="blocklisted releases" one="blocklisted release" onPage={setPage} onPageSize={setPageSize} />
      {confirmClear && (
        <Modal title="Clear the blocklist" onClose={() => setConfirmClear(false)}>
          <p className={styles.dialogText}>All blocklisted releases can be grabbed again, also by automatic searches.</p>
          <div className={styles.dialogActions}>
            <button type="button" className={ui.button} onClick={() => setConfirmClear(false)}>
              Cancel
            </button>
            <button type="button" className={ui.dangerSolid} onClick={() => clear.mutate()} disabled={clear.isPending}>
              {clear.isPending ? 'Clearing…' : 'Clear all'}
            </button>
          </div>
        </Modal>
      )}
      <Toasts toasts={toasts} onDismiss={dismiss} />
    </>
  )
}
