import { useIsFetching, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useMemo, useRef, useState } from 'react'
import { Link, Navigate, NavLink, Outlet, useLocation, useNavigate } from 'react-router'
import { api, ApiError } from '../api/client'
import { downloadKeys, downloadsApi, withLive } from '../api/downloads'
import { queueTotals } from '../activity/queueTotals'
import { libraryApi, queryKeys } from '../api/library'
import { useLiveConnection } from '../api/useLiveConnection'
import { authQueryKey, useAuth } from '../auth/auth'
import { SideBySidePill } from '../migration/SideBySide'
import { requesterMayOpen, useRole } from '../requests/roles'
import { usePendingCount } from '../requests/usePendingCount'
import { formatSpeed } from '../status/format'
import StatusWindow from '../status/StatusWindow'
import { useIsAdmin, useLiveProgress, useOngoing } from '../status/useOngoing'
import { progressDone, progressStart } from '../lib/splash'
import { toggleTheme, useTheme } from '../lib/theme'
import Icon from './Icon'
import Logo from './Logo'
import CommandPalette from './shell/CommandPalette'
import HubTabs from './shell/HubTabs'
import { groups, hubOf, hubPanelId, hubTabId, isActive, overview, requesterGroups, titleFor, type NavGroup, type NavPage } from './shell/nav'
import styles from './shell/Shell.module.css'
import ui from './ui.module.css'

const foldKey = 'streamzh-nav-folded'

function readFolded(): string[] {
  try {
    const value = JSON.parse(localStorage.getItem(foldKey) ?? '[]') as unknown
    return Array.isArray(value) ? value.filter((v): v is string => typeof v === 'string') : []
  } catch {
    return []
  }
}

/** "12 min", "1 h 05 min", "45 s": as exact as the numbers allow. */
function shortEta(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds <= 0) return ''
  if (seconds < 60) return `${Math.ceil(seconds)} s`
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ${String(Math.floor(seconds % 60)).padStart(2, '0')} s`
  return `${Math.floor(seconds / 3600)} h ${String(Math.floor((seconds % 3600) / 60)).padStart(2, '0')} min`
}

const roleLabel = { Owner: 'Owner', Admin: 'Administrator', Friend: 'Friend', Requester: 'Requester' } as const

/**
 * The StreamZH shell in the CloudZH style: a short sidebar with groups (red icons, guide line, foldable), the search
 * (Ctrl K), a sticky top bar with the page title, the live dot, downloads, the server state and the profile menu, tabs
 * in the main window for pages that belong together (Settings, System, Tools, Wanted, Indexers), and the page itself,
 * which fades in after every page change.
 */
export default function Layout() {
  // A requester (0.10) gets a reduced shell: Discover, Requests and the profile; no library, downloads or server state.
  const { requester } = useRole()
  const live = useLiveConnection()
  const pendingRequests = usePendingCount()
  const { pathname } = useLocation()
  const navigate = useNavigate()
  const { user, canSignOut } = useAuth()
  const admin = useIsAdmin()
  const theme = useTheme()
  const queryClient = useQueryClient()
  // The drawer (phone) and the profile menu belong to the page they were opened on: a page change closes them.
  const [navOpenOn, setNavOpenOn] = useState<string | null>(null)
  const [menuOn, setMenuOn] = useState<string | null>(null)
  const [palette, setPalette] = useState(false)
  const navOpen = navOpenOn === pathname
  const menu = menuOn === pathname
  const setNavOpen = (open: boolean) => setNavOpenOn(open ? pathname : null)
  const setMenu = (open: boolean) => setMenuOn(open ? pathname : null)
  const [folded, setFolded] = useState<string[]>(readFolded)

  const stats = useQuery({ queryKey: queryKeys.stats, queryFn: libraryApi.stats, enabled: !requester })
  const jobs = useQuery({ queryKey: queryKeys.jobs, queryFn: libraryApi.jobs, enabled: !requester })
  const status = useQuery({ queryKey: ['system-status'], queryFn: api.systemStatus, staleTime: 5 * 60_000, enabled: !requester })
  const { data: ongoing } = useOngoing()
  // Same query as the Activity page; the hub ("downloadChanged") refreshes it, the interval is a safety net.
  const downloads = useQuery({
    queryKey: downloadKeys.queue,
    queryFn: downloadsApi.queue,
    enabled: !requester,
    refetchInterval: (q) => (q.state.data?.items.length ? 10_000 : 60_000),
    retry: (count, error) => !(error instanceof ApiError && error.status === 404) && count < 1,
  })
  const liveProgress = useLiveProgress()
  const logout = useMutation({
    mutationFn: api.logout,
    onSettled: () => queryClient.invalidateQueries({ queryKey: authQueryKey }),
  })

  // Downloads in the top bar: overall percent from the bytes, speed and the time left from the bytes still to come.
  // Usenet and torrents together (queueTotals): paused items and a paused Usenet queue do not count for the time left.
  const dl = useMemo(() => {
    const items = (downloads.data?.items ?? []).map((i) => withLive(i, liveProgress, downloads.dataUpdatedAt))
    const active = items.filter((i) => i.status !== 'paused' && i.status !== 'queued')
    let total = 0
    let done = 0
    for (const item of items) {
      total += item.totalBytes
      done += item.totalBytes * item.progress
    }
    const totals = queueTotals(items, downloads.data?.paused ?? false)
    return {
      count: items.length,
      active: active.length,
      progress: total > 0 ? done / total : 0,
      speed: totals.speed,
      eta: totals.eta ?? Number.NaN,
    }
  }, [downloads.data, downloads.dataUpdatedAt, liveProgress])

  const busy = jobs.data && (jobs.data.running || jobs.data.queued > 0)
  const warnings = (ongoing?.notices ?? []).filter((n) => n.level !== 'info')
  const level = !live ? 'err' : warnings.some((n) => n.level === 'error') ? 'err' : warnings.length > 0 ? 'warn' : 'ok'
  const title = titleFor(pathname)
  const hub = hubOf(pathname)

  // Ctrl K / ⌘ K opens the search, like CloudZH.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault()
        setPalette((p) => !p)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  // Page change: the red bar at the top runs until the new page has its data; the drawer and menus close.
  const fetching = useIsFetching()
  const token = useRef(0)
  const waiting = useRef(false)
  useEffect(() => {
    token.current = progressStart()
    waiting.current = true
    window.scrollTo({ top: 0 })
    const fallback = window.setTimeout(() => progressDone(token.current), 8000)
    return () => window.clearTimeout(fallback)
  }, [pathname])
  useEffect(() => {
    if (!waiting.current || fetching > 0) return
    const t = window.setTimeout(() => {
      waiting.current = false
      progressDone(token.current)
    }, 120)
    return () => window.clearTimeout(t)
  }, [fetching, pathname])

  useEffect(() => {
    document.title = title === 'Overview' ? 'StreamZH' : `${title} · StreamZH`
  }, [title])

  const toggleFold = (key: string) =>
    setFolded((f) => {
      const next = f.includes(key) ? f.filter((k) => k !== key) : [...f, key]
      try {
        localStorage.setItem(foldKey, JSON.stringify(next))
      } catch {
        // no storage
      }
      return next
    })

  const item = (page: NavPage) => {
    const on = isActive(page, pathname)
    const count = page.count && stats.data ? stats.data[page.count] : null
    return (
      <Link key={page.to} to={page.to} className={`${styles.item} ${on ? styles.on : ''}`} aria-current={on ? 'page' : undefined}>
        <Icon name={page.icon} size={18} />
        <span className={styles.label}>{page.label}</span>
        {count != null && <span className={styles.count}>{count}</span>}
        {page.badge === 'requests' && pendingRequests > 0 && (
          <span className={`${styles.badge} ${styles.badgeLive}`} aria-label={`${pendingRequests} pending`}>
            {pendingRequests}
          </span>
        )}
        {page.badge === 'downloads' && dl.count > 0 && (
          <span className={`${styles.badge} ${dl.active > 0 ? styles.badgeLive : ''}`} aria-label={`${dl.count} in the queue`}>
            {dl.count}
          </span>
        )}
      </Link>
    )
  }

  const group = (g: NavGroup) => {
    const hasActive = g.pages.some((p) => isActive(p, pathname))
    const isFolded = folded.includes(g.key) && !hasActive
    return (
      <div key={g.key} className={`${styles.group} ${isFolded ? styles.folded : ''}`} role="group" aria-labelledby={`navgroup-${g.key}`}>
        <button type="button" id={`navgroup-${g.key}`} className={styles.groupHead} aria-expanded={!isFolded} onClick={() => toggleFold(g.key)}>
          <Icon name={g.icon} size={14} />
          <span>{g.title}</span>
          <Icon name="down" size={14} className={styles.chev} />
        </button>
        <div className={styles.subWrap}>
          <div className={styles.sub} {...(isFolded ? { inert: true } : {})}>
            {g.pages.map(item)}
          </div>
        </div>
      </div>
    )
  }

  const initial = user?.username.slice(0, 1).toUpperCase()

  return (
    <div className={`${styles.shell} ${navOpen ? styles.navOpen : ''}`}>
      <aside className={styles.side}>
        <div className={styles.sideIn}>
          <Link to={requester ? '/discover' : '/'} className={styles.brand} aria-label={requester ? 'StreamZH, back to Discover' : 'StreamZH, back to the overview'}>
            <Logo size={36} />
          </Link>
          <button type="button" className={styles.search} onClick={() => setPalette(true)}>
            <Icon name="search" size={16} />
            <span>Search or jump to</span>
            <kbd className={styles.kbd}>{navigator.platform.toLowerCase().includes('mac') ? '⌘' : 'Ctrl'} K</kbd>
          </button>
          <nav className={styles.nav} aria-label="Main">
            {!requester && <div className={styles.group}>{item(overview)}</div>}
            {(requester ? requesterGroups : groups).map(group)}
          </nav>
          <div className={styles.foot}>
            {busy && !admin && (
              <div className={styles.job} title={jobs.data?.running ?? undefined}>
                <span className={ui.dotBusy} />
                <span>
                  {jobs.data?.running ?? 'Working'}
                  {jobs.data && jobs.data.queued > 0 && ` · ${jobs.data.queued} queued`}
                </span>
              </div>
            )}
            <div className={styles.who}>
              <span className={styles.avatar}>{initial}</span>
              <div className={styles.whoText}>
                <b>{user?.username}</b>
                <small>{user ? roleLabel[user.role] : ''}</small>
              </div>
            </div>
            <div className={styles.acts}>
              <button type="button" className={`${ui.ghost} ${ui.sm} ${ui.icon}`} onClick={toggleTheme} title="Light or dark" aria-label="Light or dark">
                <Icon name={theme === 'dark' ? 'sun' : 'moon'} size={15} />
              </button>
              {canSignOut && (
                <button type="button" className={`${ui.ghost} ${ui.sm}`} onClick={() => logout.mutate()}>
                  <Icon name="logout" size={15} />
                  Sign out
                </button>
              )}
            </div>
          </div>
        </div>
      </aside>
      <div className={styles.drawerScrim} onClick={() => setNavOpen(false)} />

      <div className={styles.main}>
        <header className={styles.top}>
          <button type="button" className={`${ui.ghost} ${ui.icon} ${styles.menuBtn}`} aria-label="Menu" onClick={() => setNavOpen(!navOpen)}>
            <Icon name="menu" />
          </button>
          <div className={styles.topTitle}>{title}</div>
          {!requester && (
            <span className={live ? styles.liveDot : styles.liveDotOff} role="status" title={live ? 'Live: pages update by themselves' : 'Offline: reconnecting …'} />
          )}
          <div className={styles.spacer} />

          {admin && <SideBySidePill />}

          {dl.count > 0 && (
            <NavLink to="/activity" className={styles.dlPill} title={`${dl.count} in the queue`}>
              <Icon name="download" size={15} />
              <span>{Math.floor(dl.progress * 100)} %</span>
              <span className={styles.dlBar} aria-hidden="true">
                <i style={{ width: `${Math.max(2, dl.progress * 100)}%` }} />
              </span>
              <span className={`${styles.dlText} ${styles.dlMuted}`}>
                {dl.speed > 0 ? `${formatSpeed(dl.speed)} · ${shortEta(dl.eta)}` : dl.active > 0 ? 'starting …' : 'paused'}
              </span>
            </NavLink>
          )}

          {!requester && (
            <div className={`${styles.srvbar} ${level === 'warn' ? styles.srvWarn : level === 'err' ? styles.srvErr : ''}`}>
              <button
                type="button"
                className={styles.srvMain}
                onClick={() => void navigate(admin ? '/system/status' : '/')}
                title={warnings.map((w) => w.title).join('\n') || undefined}
              >
                <span className={styles.srvDot} aria-hidden="true" />
                <b>StreamZH</b>
                <span className={styles.srvState}>
                  {!live ? 'offline' : warnings.length > 0 ? `${warnings.length} ${warnings.length === 1 ? 'warning' : 'warnings'}` : 'online'}
                </span>
                {stats.data && (
                  <span className={styles.srvSeg}>
                    <Icon name="film" size={13} />
                    {stats.data.movies}
                    <Icon name="tv" size={13} />
                    {stats.data.series}
                  </span>
                )}
              </button>
              {status.data && <span className={styles.srvVer}>v{status.data.version.split('+')[0]}</span>}
            </div>
          )}

          <button type="button" className={`${ui.ghost} ${ui.icon}`} onClick={toggleTheme} aria-label="Switch colour scheme" title="Light or dark">
            <Icon name={theme === 'dark' ? 'sun' : 'moon'} />
          </button>

          <ProfileMenu
            open={menu}
            setOpen={setMenu}
            initial={initial}
            name={user?.username ?? ''}
            role={user ? roleLabel[user.role] : ''}
            admin={admin}
            canSignOut={canSignOut}
            onSearch={() => setPalette(true)}
            onSignOut={() => logout.mutate()}
          />
        </header>

        {hub && <HubTabs hub={hub.hub} section={hub.section} tab={hub.tab} />}

        <div
          key={pathname}
          className="v-in"
          {...(hub ? { id: hubPanelId, role: 'tabpanel', 'aria-labelledby': hubTabId(hub.tab.to) } : {})}
        >
          {requester && !requesterMayOpen(pathname) ? <Navigate to="/discover" replace /> : <Outlet />}
        </div>
      </div>

      {palette && <CommandPalette requester={requester} onClose={() => setPalette(false)} />}
      <StatusWindow />
    </div>
  )
}

function ProfileMenu({
  open,
  setOpen,
  initial,
  name,
  role,
  admin,
  canSignOut,
  onSearch,
  onSignOut,
}: {
  open: boolean
  setOpen: (open: boolean) => void
  initial: string | undefined
  name: string
  role: string
  admin: boolean
  canSignOut: boolean
  onSearch: () => void
  onSignOut: () => void
}) {
  const wrap = useRef<HTMLDivElement>(null)
  const navigate = useNavigate()

  useEffect(() => {
    if (!open) return
    const onDown = (e: MouseEvent) => {
      if (!wrap.current?.contains(e.target as Node)) setOpen(false)
    }
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false)
    document.addEventListener('mousedown', onDown)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onDown)
      document.removeEventListener('keydown', onKey)
    }
  }, [open, setOpen])

  const go = (to: string) => {
    setOpen(false)
    void navigate(to)
  }

  return (
    <div className={styles.pmWrap} ref={wrap}>
      <button type="button" className={styles.pbtn} aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen(!open)} aria-label={`Profile of ${name}`}>
        <span className={styles.avatar}>{initial}</span>
        <span className={styles.pbtnName}>{name}</span>
        <Icon name="down" size={14} />
      </button>
      {open && (
        <div className={styles.pmenu} role="menu">
          <div className={styles.pmHead}>
            <span className={styles.avatarLg}>{initial}</span>
            <div>
              <b>{name}</b>
              <small>{role}</small>
            </div>
          </div>
          <div className={styles.pmSep} />
          <button type="button" role="menuitem" className={styles.pmItem} onClick={() => go('/profile')}>
            <Icon name="user" />
            Profile
          </button>
          <button type="button" role="menuitem" className={styles.pmItem} onClick={() => (setOpen(false), onSearch())}>
            <Icon name="search" />
            Search or jump to
          </button>
          {admin && (
            <>
              <button type="button" role="menuitem" className={styles.pmItem} onClick={() => go('/settings')}>
                <Icon name="sliders" />
                Settings
              </button>
              <button type="button" role="menuitem" className={styles.pmItem} onClick={() => go('/system/status')}>
                <Icon name="heart" />
                System status
              </button>
            </>
          )}
          <button type="button" role="menuitem" className={styles.pmItem} onClick={toggleTheme}>
            <Icon name="moon" />
            Light or dark mode
          </button>
          {canSignOut && (
            <>
              <div className={styles.pmSep} />
              <button type="button" role="menuitem" className={`${styles.pmItem} ${styles.pmDanger}`} onClick={onSignOut}>
                <Icon name="logout" />
                Sign out
              </button>
            </>
          )}
        </div>
      )}
    </div>
  )
}
