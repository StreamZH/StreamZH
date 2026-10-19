import { statusLabels, type RequestStatus } from '../api/requests'
import ui from '../components/ui.module.css'
import styles from './Requests.module.css'

const tones: Record<RequestStatus, string> = {
  pending: ui.pillWarn,
  approved: ui.pillBusy,
  processing: ui.pillBusy,
  partiallyAvailable: styles.pillInfo,
  available: ui.pillOk,
  declined: styles.pillMuted,
  failed: ui.pillErr,
}

/** The state of a request as a CloudZH pill. */
export default function StatusChip({ status, title }: { status: RequestStatus; title?: string }) {
  return (
    <span className={`${ui.pill} ${tones[status] ?? ''} ${styles.chip}`} title={title}>
      {status === 'processing' && <span className={styles.chipBusy} aria-hidden="true" />}
      {statusLabels[status] ?? status}
    </span>
  )
}
