import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { requesterName, requestKeys, requestsApi, seasonsText, type RequestFilter, type RequestMediaType } from '../api/requests'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import { formatDateTime, relativeTime } from '../lib/format'
import StatusChip from './StatusChip'
import styles from './Requests.module.css'

/**
 * Who requested this movie or series, which seasons and where each request stands; on the library detail pages.
 * Shows nothing when nobody requested it (or the requests API is not there).
 */
export default function RequestsPanel({ mediaType, tmdbId }: { mediaType: RequestMediaType; tmdbId: number }) {
  const filter: RequestFilter = { mediaType, tmdbId, pageSize: 100 }
  const list = useQuery({ queryKey: requestKeys.list(filter), queryFn: () => requestsApi.list(filter), retry: false })
  // Filtered here too, in case the server ignores the tmdbId filter.
  const records = (list.data?.records ?? []).filter((r) => r.tmdbId === tmdbId && r.mediaType === mediaType)
  if (records.length === 0) return null

  return (
    <section className={ui.panel}>
      <div className={ui.panelHead}>
        <h2 className={styles.panelTitle}>
          <Icon name="inbox" size={17} />
          Requests
        </h2>
        <span className={styles.panelCount}>{records.length}</span>
      </div>
      <ul className={styles.panelList}>
        {records.map((r) => (
          <li key={r.id}>
            <div className={styles.who}>
              <span className={styles.avatar} aria-hidden="true">
                {requesterName(r).slice(0, 1).toUpperCase()}
              </span>
              <div className={styles.whoText}>
                <b>{requesterName(r)}</b>
                <span className={styles.when} title={formatDateTime(r.created)}>
                  {relativeTime(r.created)}
                  {r.source === 'discord' && (
                    <span className={styles.source}>
                      <Icon name="chat" size={12} />
                      Discord
                    </span>
                  )}
                </span>
              </div>
            </div>
            {mediaType === 'series' && <span className={styles.panelSeasons}>{seasonsText(r.mediaType, r.seasons)}</span>}
            <StatusChip status={r.status} title={r.reason ?? undefined} />
          </li>
        ))}
      </ul>
      <Link to={`/requests?tab=all&type=${mediaType}`} className={styles.panelLink}>
        All requests
        <Icon name="right" size={13} />
      </Link>
    </section>
  )
}
