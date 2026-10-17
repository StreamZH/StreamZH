import { useQuery } from '@tanstack/react-query'
import { useRef, type KeyboardEvent } from 'react'
import { useSearchParams } from 'react-router'
import { downloadKeys, downloadsApi } from '../api/downloads'
import page from '../pages/Page.module.css'
import HistoryTab from './HistoryTab'
import QueueTab from './QueueTab'
import styles from './Activity.module.css'

const tabs = [
  { key: 'queue', label: 'Queue' },
  { key: 'history', label: 'History' },
] as const

type TabKey = (typeof tabs)[number]['key']

/** Downloads in progress (Queue) and what finished, failed or was grabbed (History). */
export default function ActivityPage() {
  const [url, setUrl] = useSearchParams()
  const tab: TabKey = url.get('tab') === 'history' ? 'history' : 'queue'
  const queue = useQuery({ queryKey: downloadKeys.queue, queryFn: downloadsApi.queue, enabled: false })
  const count = queue.data?.items.length ?? 0
  const refs = useRef<Record<TabKey, HTMLButtonElement | null>>({ queue: null, history: null })

  const select = (key: TabKey) => setUrl(key === 'queue' ? {} : { tab: key })

  // Arrow keys move between the tabs (WAI-ARIA tabs pattern).
  const onKeyDown = (e: KeyboardEvent) => {
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight' && e.key !== 'Home' && e.key !== 'End') return
    e.preventDefault()
    const index = tabs.findIndex((t) => t.key === tab)
    const next =
      e.key === 'Home' ? 0 : e.key === 'End' ? tabs.length - 1 : (index + (e.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length
    select(tabs[next].key)
    refs.current[tabs[next].key]?.focus()
  }

  return (
    <div className={`${page.page} ${styles.activity}`}>
      <header>
        <p className={styles.eyebrow}>System</p>
        <h1 className={page.title}>Activity</h1>
        <p className={page.lead}>Downloads in progress, from grab to unpack, and everything that finished or failed.</p>
      </header>

      <div className={styles.tabs} role="tablist" aria-label="Activity" onKeyDown={onKeyDown}>
        {tabs.map((t) => (
          <button
            key={t.key}
            ref={(el) => {
              refs.current[t.key] = el
            }}
            type="button"
            role="tab"
            id={`activity-tab-${t.key}`}
            aria-selected={tab === t.key}
            aria-controls={`activity-panel-${t.key}`}
            tabIndex={tab === t.key ? 0 : -1}
            className={styles.tab}
            onClick={() => select(t.key)}
          >
            {t.label}
            {t.key === 'queue' && count > 0 && <span className={styles.tabCount}>{count}</span>}
          </button>
        ))}
      </div>

      <section id={`activity-panel-${tab}`} role="tabpanel" aria-labelledby={`activity-tab-${tab}`} className={styles.panel}>
        {tab === 'queue' ? <QueueTab /> : <HistoryTab />}
      </section>
    </div>
  )
}
