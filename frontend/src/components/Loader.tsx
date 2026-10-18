import { useId } from 'react'
import { MARK_H as H, MARK_Z as Z } from './logoPaths'

/**
 * Waiting, the CloudZH way: the StreamZH monogram whose Z draws itself and dissolves again (styles/motion.css .ld).
 * size: 'row' small in a line, 'lg' large.
 */
export default function Loader({ text, size }: { text?: string; size?: 'row' | 'lg' }) {
  const id = useId().replace(/:/g, '')
  return (
    <div className={`ld${size ? ` ${size}` : ''}`} role="status" aria-label={text ?? 'Loading'}>
      <svg className="ld-m" viewBox="0 0 100 100" aria-hidden="true">
        <defs>
          <mask id={`ldm${id}`} maskUnits="userSpaceOnUse" x="-10" y="-10" width="120" height="120">
            <path className="ld-s" pathLength={1} d="M17 8.5 L71.85 8.5 L28.15 91.5 L83 91.5" />
          </mask>
        </defs>
        <path className="ld-h" d={H} />
        <path className="ld-z" mask={`url(#ldm${id})`} d={Z} />
      </svg>
      {text && <span>{text}</span>}
    </div>
  )
}

/** Shimmering placeholder rows instead of «Loading …». */
export function Skeleton({ rows = 3 }: { rows?: number }) {
  const widths = ['90%', '70%', '80%', '55%', '75%', '60%']
  return (
    <div style={{ display: 'grid', gap: 10, padding: '2px 0' }} role="status" aria-label="Loading">
      {Array.from({ length: rows }, (_, i) => (
        <span key={i} className="sk" style={{ width: widths[i % widths.length] }} />
      ))}
    </div>
  )
}
