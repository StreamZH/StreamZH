import styles from './DownloadClients.module.css'

const marks: Record<string, { text: string; tone: string }> = {
  streamzh: { text: 'Z', tone: styles.markAccent },
  qbittorrent: { text: 'qB', tone: styles.markInfo },
  transmission: { text: 'Tr', tone: styles.markWarn },
  deluge: { text: 'De', tone: styles.markOk },
  usenet: { text: 'NZB', tone: styles.markInfo },
}

/** Monogram square for a client implementation (no third-party logos). */
export function ClientMark({ implementation, size = 32 }: { implementation: string; size?: number }) {
  const mark = marks[implementation.toLowerCase()] ?? { text: implementation.slice(0, 2), tone: styles.markMuted }
  return (
    <span className={`${styles.mark} ${mark.tone}`} style={{ width: size, height: size }} aria-hidden="true">
      {mark.text}
    </span>
  )
}
