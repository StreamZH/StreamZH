import type { ReactNode } from 'react'
import { standardCategoryNames, topCategories, type IndexerPrivacy, type IndexerProtocol } from '../api/indexers'
import { privacyLabels, protocolLabels, topCategoryOf } from './labels'
import styles from './Chips.module.css'

const categoryTone: Record<number, string> = {
  2000: styles.movies,
  3000: styles.audio,
  5000: styles.tv,
  7000: styles.books,
}

export function ProtocolChip({ protocol }: { protocol: IndexerProtocol }) {
  return <span className={`${styles.chip} ${protocol === 'torrent' ? styles.torrent : styles.usenet}`}>{protocolLabels[protocol]}</span>
}

export function PrivacyChip({ privacy }: { privacy: IndexerPrivacy }) {
  const tone = privacy === 'private' ? styles.private : privacy === 'semiPrivate' ? styles.semiPrivate : styles.public
  return <span className={`${styles.chip} ${tone}`}>{privacyLabels[privacy]}</span>
}

export function CategoryChip({ id }: { id: number }) {
  const top = topCategoryOf(id)
  return (
    <span className={`${styles.chip} ${styles.category} ${categoryTone[top] ?? ''}`} title={`Newznab ${id}`}>
      {standardCategoryNames[id] ?? standardCategoryNames[top] ?? id}
    </span>
  )
}

/** Top-level categories as chips, in Newznab order. */
export function CategoryChips({ ids }: { ids: number[] }) {
  const tops = topCategories.filter((t) => ids.some((id) => topCategoryOf(id) === t))
  if (tops.length === 0) return <span className={styles.none}>–</span>
  return (
    <span className={styles.chips}>
      {tops.map((id) => (
        <CategoryChip key={id} id={id} />
      ))}
    </span>
  )
}

/** Small status pill: tone ok / warn / error / off. */
export function Pill({ tone, children, title }: { tone: 'ok' | 'warn' | 'error' | 'off' | 'info'; children: ReactNode; title?: string }) {
  return (
    <span className={`${styles.pill} ${styles[tone]}`} title={title}>
      {children}
    </span>
  )
}
