import { NavLink, Outlet } from 'react-router'
import { useLiveConnection } from '../api/useLiveConnection'
import Icon, { type IconName } from './Icon'
import styles from './Layout.module.css'

interface NavItem {
  to: string
  label: string
  icon: IconName
  color?: string
}

const sections: { title?: string; items: NavItem[] }[] = [
  { items: [{ to: '/', label: 'Home', icon: 'home' }] },
  {
    title: 'Library',
    items: [
      { to: '/library/movies', label: 'Movies', icon: 'movies', color: 'var(--movies)' },
      { to: '/library/series', label: 'Series', icon: 'series', color: 'var(--series)' },
    ],
  },
  {
    title: 'System',
    items: [
      { to: '/activity', label: 'Activity', icon: 'activity' },
      { to: '/indexers', label: 'Indexers', icon: 'indexers' },
      { to: '/settings', label: 'Settings', icon: 'settings' },
    ],
  },
]

export default function Layout() {
  const live = useLiveConnection()

  return (
    <div className={styles.shell}>
      <nav className={styles.sidebar} aria-label="Main">
        <div className={styles.brand}>
          <div className={styles.tile}>ZH</div>
          <div className={styles.wordmark}>
            Stream<span>ZH</span>
          </div>
        </div>

        {sections.map((section, index) => (
          <div key={section.title ?? index} className={styles.section}>
            {section.title && <div className={styles.sectionTitle}>{section.title}</div>}
            {section.items.map((item) => (
              <NavLink
                key={item.to}
                to={item.to}
                end={item.to === '/'}
                className={({ isActive }) => (isActive ? `${styles.link} ${styles.active}` : styles.link)}
                style={item.color ? { ['--item-color' as string]: item.color } : undefined}
              >
                <Icon name={item.icon} />
                {item.label}
              </NavLink>
            ))}
          </div>
        ))}

        <div className={styles.footer}>
          <span className={live ? styles.dotOk : styles.dotOff} />
          {live ? 'Live' : 'Offline'}
        </div>
      </nav>

      <main className={styles.main}>
        <Outlet />
      </main>
    </div>
  )
}
