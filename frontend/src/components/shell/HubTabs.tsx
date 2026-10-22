import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react'
import { Link } from 'react-router'
import { advancedLabel } from '../../lib/advancedMode'
import { useShowMigration } from '../../migration/hooks'
import Icon from '../Icon'
import type { IconName } from '../Icon'
import { hubPanelId, hubTabId, type Hub, type HubSection, type HubTab } from './nav'
import styles from './Shell.module.css'

interface Item {
  id: string
  to: string
  label: string
  icon?: IconName
  on: boolean
  /** Shown because the Advanced switch is on: gets the small «Advanced» tag. */
  advanced?: boolean
}

/**
 * The tabs below the top bar (CloudZH .hubtabs) for pages that belong together. Every tab is its own address, so links
 * from elsewhere and the back button keep working. A hub with sections (Settings) shows two rows: the sections, then
 * the pages of the open section. Keyboard: arrow keys, Home and End move between the tabs of a row, Enter or Space
 * opens one (WAI-ARIA tabs with manual activation, since opening a tab loads a page).
 */
export default function HubTabs({ hub, section, tab }: { hub: Hub; section: HubSection; tab: HubTab }) {
  const hasAdvanced = hub.sections.some((s) => s.advanced)
  const migration = useShowMigration(hasAdvanced)
  // An advanced section stays visible while it is open, so a link to it never shows a page without its tab.
  const sections = hub.sections.filter((s) => !s.advanced || migration.show || s === section)
  const twoRows = sections.length > 1
  const pages: Item[] = section.tabs.map((t) => ({ id: hubTabId(t.to), to: t.to, label: t.label, icon: twoRows ? undefined : t.icon, on: t === tab }))

  return (
    <nav className={styles.hubnav} aria-label={`${hub.title} pages`}>
      {twoRows && (
        <TabRow
          label={hub.title}
          variant="main"
          tight={section.tabs.length > 1}
          items={sections.map((s) => ({
            id: s.tabs.length > 1 ? `hubsec-${s.key}` : hubTabId(s.tabs[0].to),
            to: s.tabs[0].to,
            label: s.label,
            icon: s.icon,
            on: s === section,
            advanced: s.advanced && !migration.unfinished,
          }))}
        />
      )}
      {section.tabs.length > 1 && <TabRow label={twoRows ? `${hub.title}: ${section.label}` : hub.title} variant={twoRows ? 'sub' : 'main'} items={pages} />}
    </nav>
  )
}

function TabRow({ label, items, variant, tight = false }: { label: string; items: Item[]; variant: 'main' | 'sub'; tight?: boolean }) {
  const row = useRef<HTMLDivElement>(null)
  const current = items.find((i) => i.on)?.id
  // Fades at the edges while there are more tabs to scroll to (phone).
  const [more, setMore] = useState({ left: false, right: false })

  useEffect(() => {
    const el = row.current
    if (!el) return
    const update = () => {
      const left = el.scrollLeft > 2
      const right = el.scrollLeft + el.clientWidth < el.scrollWidth - 2
      setMore((m) => (m.left === left && m.right === right ? m : { left, right }))
    }
    update()
    el.addEventListener('scroll', update, { passive: true })
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(update)
    observer?.observe(el)
    return () => {
      el.removeEventListener('scroll', update)
      observer?.disconnect()
    }
  }, [items.length])

  // On a phone the row scrolls sideways: keep the open tab in view.
  useLayoutEffect(() => {
    const el = row.current
    const on = el?.querySelector<HTMLElement>('[aria-selected="true"]')
    if (!el || !on || el.scrollWidth <= el.clientWidth) return
    const left = on.offsetLeft - el.offsetLeft
    if (left < el.scrollLeft || left + on.offsetWidth > el.scrollLeft + el.clientWidth) el.scrollLeft = left - (el.clientWidth - on.offsetWidth) / 2
  }, [current])

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    const tabs = Array.from(e.currentTarget.querySelectorAll<HTMLElement>('[role="tab"]'))
    const index = tabs.indexOf(document.activeElement as HTMLElement)
    if (index < 0) return
    if (e.key === ' ') {
      e.preventDefault()
      tabs[index].click()
      return
    }
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight' && e.key !== 'Home' && e.key !== 'End') return
    e.preventDefault()
    const next = e.key === 'Home' ? 0 : e.key === 'End' ? tabs.length - 1 : (index + (e.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length
    tabs[next].focus()
  }

  return (
    <div
      ref={row}
      role="tablist"
      aria-label={label}
      className={[
        variant === 'main' ? styles.hubtabs : styles.hubsub,
        tight ? styles.hubtabsTight : '',
        more.left ? styles.moreLeft : '',
        more.right ? styles.moreRight : '',
      ].join(' ')}
      onKeyDown={onKeyDown}
    >
      {items.map((i) => (
        <Link
          key={i.id}
          id={i.id}
          to={i.to}
          role="tab"
          aria-selected={i.on}
          aria-controls={i.on ? hubPanelId : undefined}
          tabIndex={i.on ? 0 : -1}
          className={variant === 'main' ? `${styles.hubtab} ${i.on ? styles.hubtabOn : ''}` : `${styles.hubsubtab} ${i.on ? styles.hubsubOn : ''}`}
        >
          {i.icon && <Icon name={i.icon} size={15} />}
          <span className={i.advanced ? advancedLabel : undefined}>{i.label}</span>
        </Link>
      ))}
    </div>
  )
}
