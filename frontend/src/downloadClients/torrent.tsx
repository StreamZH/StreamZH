import type { DownloadProtocol } from '../api/downloadClients'
import { formatRatio, seedTone, type TrackerFlag } from './torrentFormat'
import styles from './Torrent.module.css'

// Small pieces every torrent-aware list uses: the protocol icon, seeders/peers coloured by health, the freeleech and
// other tracker flags, and the ratio. Kept in one place so release tables, Activity and the settings look alike.

/** Usenet: a stack of articles; torrent: a magnet. Inline SVG in the CloudZH icon style (stroke 1.7). */
export function ProtocolIcon({ protocol, size = 14, label = true }: { protocol: DownloadProtocol | null | undefined; size?: number; label?: boolean }) {
  const torrent = protocol === 'torrent'
  const name = torrent ? 'Torrent' : 'Usenet'
  return (
    <span className={`${styles.protocol} ${torrent ? styles.torrent : styles.usenet}`} title={name} aria-label={label ? name : undefined} role={label ? 'img' : undefined}>
      <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.9" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
        {torrent ? (
          <>
            <path d="M6 3v8a6 6 0 0 0 12 0V3" />
            <path d="M6 7h4M14 7h4" />
            <path d="M10 3v8a2 2 0 0 0 4 0V3" />
          </>
        ) : (
          <>
            <rect x="4" y="3" width="16" height="18" rx="2" />
            <path d="M8 8h8M8 12h8M8 16h5" />
          </>
        )}
      </svg>
    </span>
  )
}

/** Text chip "Torrent" / "Usenet" with the icon. */
export function ProtocolTag({ protocol }: { protocol: DownloadProtocol | null | undefined }) {
  const torrent = protocol === 'torrent'
  return (
    <span className={`${styles.protocolTag} ${torrent ? styles.torrent : styles.usenet}`}>
      <ProtocolIcon protocol={protocol} size={12} label={false} />
      {torrent ? 'Torrent' : 'Usenet'}
    </span>
  )
}

const seedClass = { dead: styles.dead, low: styles.low, ok: styles.ok, unknown: styles.unknown }

const count = new Intl.NumberFormat()

/** "128 / 14": seeders (coloured) and peers. */
export function Peers({ seeders, peers, compact = false }: { seeders: number | null | undefined; peers: number | null | undefined; compact?: boolean }) {
  const tone = seedTone(seeders)
  const title = `${seeders ?? '?'} seeder${seeders === 1 ? '' : 's'}, ${peers ?? '?'} peer${peers === 1 ? '' : 's'}`
  return (
    <span className={`${styles.peers} ${seedClass[tone]}`} title={title} aria-label={title}>
      <span className={styles.seeders}>
        {!compact && <span className={styles.dot} aria-hidden="true" />}
        {seeders == null ? '?' : count.format(seeders)}
      </span>
      <span className={styles.slash} aria-hidden="true">
        /
      </span>
      <span className={styles.leechers}>{peers == null ? '?' : count.format(peers)}</span>
    </span>
  )
}

const flagClass = { free: styles.flagFree, up: styles.flagUp, info: styles.flagInfo, bad: styles.flagBad }

export function TrackerFlags({ flags }: { flags: TrackerFlag[] }) {
  if (flags.length === 0) return null
  return (
    <span className={styles.flags}>
      {flags.map((f) => (
        <span key={f.key} className={`${styles.flag} ${flagClass[f.tone]}`} title={f.title}>
          {f.label}
        </span>
      ))}
    </span>
  )
}

/** Ratio coloured: below 1 still owing, from 1 on fine. */
export function Ratio({ ratio }: { ratio: number | null | undefined }) {
  const tone = ratio == null ? '' : ratio >= 1 ? styles.ratioOk : styles.ratioLow
  return (
    <span className={`${styles.ratio} ${tone}`} title="Upload ratio">
      {formatRatio(ratio)}
    </span>
  )
}
