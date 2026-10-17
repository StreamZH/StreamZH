import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { NavLink, Outlet, useLocation } from 'react-router'
import { api } from '../api/client'
import { libraryApi, queryKeys } from '../api/library'
import { authQueryKey, useAuth } from '../auth/auth'
import { useLiveConnection } from '../api/useLiveConnection'
import Icon, { type IconName } from './Icon'
import Logo from './Logo'
import styles from './Layout.module.css'

interface NavItem {
  to: string
  label: string
  icon: IconName
  color?: string
  count?: 'movies' | 'series'
  /** Sub-pages, listed under the item while one of them is open (like Prowlarr's sidebar). */
  children?: { to: string; label: string }[]
}

const sections: { title?: string; items: NavItem[] }[] = [
  { items: [{ to: '/', label: 'Home', icon: 'home' }] },
  {
    title: 'Library',
    items: [
      { to: '/library/movies', label: 'Movies', icon: 'movies', color: 'var(--movies)', count: 'movies' },
      { to: '/library/series', label: 'Series', icon: 'series', color: 'var(--series)', count: 'series' },
    ],
  },
  {
    title: 'System',
    items: [
      { to: '/activity', label: 'Activity', icon: 'activity' },
      {
        to: '/indexers',
        label: 'Indexers',
        icon: 'indexers',
        children: [
          { to: '/indexers', label: 'Indexers' },
          { to: '/indexers/search', label: 'Search' },
          { to: '/indexers/stats', label: 'Stats' },
          { to: '/indexers/history', label: 'History' },
        ],
      },
      { to: '/settings', label: 'Settings', icon: 'settings' },
    ],
  },
]

export default function Layout() {
  const live = useLiveConnection()
  const { pathname } = useLocation()
  const { user } = useAuth()
  const stats = useQuery({ queryKey: queryKeys.stats, queryFn: libraryApi.stats })
  const jobs = useQuery({ queryKey: queryKeys.jobs, queryFn: libraryApi.jobs })
  const busy = jobs.data && (jobs.data.running || jobs.data.queued > 0)
  const queryClient = useQueryClient()
  const logout = useMutation({
    mutationFn: api.logout,
    onSettled: () => queryClient.invalidateQueries({ queryKey: authQueryKey }),
  })

  return (
    <div className={styles.shell}>
      <nav className={styles.sidebar} aria-label="Main">
        <div className={styles.brand}>
          <Logo size={28} />
        </div>

        {sections.map((section, index) => (
          <div key={section.title ?? index} className={styles.section}>
            {section.title && <div className={styles.sectionTitle}>{section.title}</div>}
            {section.items.map((item) => {
              const open = item.children && (pathname === item.to || pathname.startsWith(`${item.to}/`))
              return [
                <NavLink
                  key={item.to}
                  to={item.to}
                  end={item.to === '/'}
                  className={({ isActive }) => (isActive ? `${styles.link} ${styles.active}` : styles.link)}
                  style={item.color ? { ['--item-color' as string]: item.color } : undefined}
                >
                  <Icon name={item.icon} />
                  <span className={styles.label}>{item.label}</span>
                  {item.count && stats.data && <span className={styles.count}>{stats.data[item.count]}</span>}
                </NavLink>,
                open && (
                  <div key={`${item.to}-sub`} className={styles.sub} aria-label={`${item.label} pages`} role="group">
                    {item.children!.map((child) => (
                      <NavLink
                        key={child.to}
                        to={child.to}
                        end
                        className={({ isActive }) => (isActive ? `${styles.subLink} ${styles.subActive}` : styles.subLink)}
                      >
                        {child.label}
                      </NavLink>
                    ))}
                  </div>
                ),
              ]
            })}
          </div>
        ))}

        <div className={styles.footer}>
          <div className={styles.user}>
            <span className={styles.avatar}>{user?.username.slice(0, 1).toUpperCase()}</span>
            <span className={styles.userName}>{user?.username}</span>
            <button className={styles.logout} type="button" onClick={() => logout.mutate()} title="Sign out">
              Sign out
            </button>
          </div>
          {busy && (
            <div className={styles.job} title={jobs.data?.running ?? undefined}>
              <span className={styles.spinner} />
              <span className={styles.jobText}>
                {jobs.data?.running ?? 'Working'}
                {jobs.data && jobs.data.queued > 0 && ` · ${jobs.data.queued} queued`}
              </span>
            </div>
          )}
          <div className={styles.live}>
            <span className={live ? styles.dotOk : styles.dotOff} />
            {live ? 'Live' : 'Offline'}
          </div>
        </div>
      </nav>

      <main className={styles.main}>
        <Outlet />
      </main>
    </div>
  )
}
