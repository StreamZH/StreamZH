import { useId } from 'react'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
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
 * CloudZH-style pager: "1–50 of 214 downloads", small Previous/Next and page buttons, and the page size. Shown whenever
 * the list has items, so the page size can always be changed. Compact (‹ 2/5 ›) at phone width.
 */
export default function Pager({ page, pageSize, total, noun, one, onPage, onPageSize, sizes = pageSizes }: PagerProps) {
  const sizeId = useId()
  if (total <= 0) return null
  const pages = Math.max(1, Math.ceil(total / pageSize))
  const current = Math.min(Math.max(1, page), pages)
  const from = (current - 1) * pageSize + 1
  const to = Math.min(total, current * pageSize)
  const word = total === 1 ? (one ?? noun.replace(/s$/, '')) : noun

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
            <b>
              {numberFormat.format(from)}–{numberFormat.format(to)}
            </b>{' '}
            of {numberFormat.format(total)} {word}
          </>
        ) : (
          <>
            <b>{numberFormat.format(total)}</b> {word}
          </>
        )}
      </p>

      {pages > 1 && (
        <div className={styles.pages}>
          <button
            type="button"
            className={`${ui.button} ${ui.sm} ${ui.icon}`}
            disabled={current <= 1}
            onClick={() => onPage(current - 1)}
            aria-label="Previous page"
            title="Previous page"
          >
            <Icon name="left" size={15} />
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
          <button
            type="button"
            className={`${ui.button} ${ui.sm} ${ui.icon}`}
            disabled={current >= pages}
            onClick={() => onPage(current + 1)}
            aria-label="Next page"
            title="Next page"
          >
            <Icon name="right" size={15} />
          </button>
        </div>
      )}

      <div className={styles.size}>
        <label htmlFor={sizeId}>Per page</label>
        <select id={sizeId} className={`${ui.select} ${styles.select}`} value={pageSize} onChange={(e) => changeSize(Number(e.target.value))}>
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
