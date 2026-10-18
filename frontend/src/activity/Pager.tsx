import { useId } from 'react'
import { pageList, pageSizes } from './paging'
import styles from './Pager.module.css'

export interface PagerProps {
  /** Current page, starting at 1. */
  page: number
  pageSize: number
  /** All items of the (filtered) list. */
  total: number
  /** Plural noun for the summary ("downloads"). */
  noun: string
  /** Singular noun; defaults to the plural without its last letter. */
  one?: string
  onPage: (page: number) => void
  /** Changes the page size; the pager also moves to the page that keeps the first visible item in view. */
  onPageSize: (size: number) => void
  sizes?: readonly number[]
}

const numberFormat = new Intl.NumberFormat()

/**
 * Numbered pages with Previous/Next, "Page 2 of 5 · 237 downloads" and the page size. Shown whenever the list has
 * items, so the page size can always be changed. Compact (‹ 2/5 ›) at phone width.
 */
export default function Pager({ page, pageSize, total, noun, one, onPage, onPageSize, sizes = pageSizes }: PagerProps) {
  const sizeId = useId()
  if (total <= 0) return null
  const pages = Math.max(1, Math.ceil(total / pageSize))
  const current = Math.min(Math.max(1, page), pages)
  const count = `${numberFormat.format(total)} ${total === 1 ? (one ?? noun.replace(/s$/, '')) : noun}`

  const changeSize = (size: number) => {
    const first = (current - 1) * pageSize
    onPageSize(size)
    onPage(Math.floor(first / size) + 1)
  }

  return (
    <nav className={styles.pager} aria-label="Pages">
      <p className={styles.summary} aria-live="polite">
        {pages > 1 ? (
          <>
            Page <strong>{current}</strong> of {numberFormat.format(pages)} · {count}
          </>
        ) : (
          count
        )}
      </p>

      {pages > 1 && (
        <div className={styles.pages}>
          <button type="button" className={styles.step} disabled={current <= 1} onClick={() => onPage(current - 1)} aria-label="Previous page" title="Previous page">
            <Chevron left />
          </button>
          <ol className={styles.numbers}>
            {pageList(current, pages).map((p, index) =>
              p === 'gap' ? (
                <li key={`gap-${index}`} className={styles.gap} aria-hidden="true">
                  …
                </li>
              ) : (
                <li key={p}>
                  <button
                    type="button"
                    className={styles.number}
                    aria-current={p === current ? 'page' : undefined}
                    aria-label={`Page ${p}`}
                    onClick={() => p !== current && onPage(p)}
                  >
                    {numberFormat.format(p)}
                  </button>
                </li>
              ),
            )}
          </ol>
          <span className={styles.compact} aria-hidden="true">
            {current}/{pages}
          </span>
          <button type="button" className={styles.step} disabled={current >= pages} onClick={() => onPage(current + 1)} aria-label="Next page" title="Next page">
            <Chevron />
          </button>
        </div>
      )}

      <div className={styles.size}>
        <label htmlFor={sizeId}>Per page</label>
        <select id={sizeId} className={styles.select} value={pageSize} onChange={(e) => changeSize(Number(e.target.value))}>
          {sizes.map((s) => (
            <option key={s} value={s}>
              {s}
            </option>
          ))}
        </select>
      </div>
    </nav>
  )
}

function Chevron({ left }: { left?: boolean }) {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d={left ? 'm15 6-6 6 6 6' : 'm9 6 6 6-6 6'} />
    </svg>
  )
}
