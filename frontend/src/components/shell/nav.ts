// The navigation of StreamZH, built like CloudZH's sidebar: an overview on top, then groups with a title and a red
// icon, pages indented on a guide line. Pages with sub-pages show them as tabs below the top bar (hub tabs).
import { settingsAreas, systemAreas } from '../../settings/areas'
import type { IconName } from '../Icon'

export interface NavPage {
  to: string
  label: string
  icon: IconName
  /** Count next to the label (library size) or a red badge (active downloads). */
  count?: 'movies' | 'series'
  badge?: 'downloads'
  /** Also active for these path prefixes. */
  match?: string[]
  /** Words the command palette finds this page by. */
  keywords?: string
}

export interface NavGroup {
  key: string
  title: string
  icon: IconName
  /** Folded groups show only their title until opened (remembered per browser), unless a page in them is open. */
  foldable?: boolean
  pages: NavPage[]
}

export const overview: NavPage = { to: '/', label: 'Overview', icon: 'home', keywords: 'home dashboard start' }

const settingsIcons: Record<string, IconName> = {
  '/settings/media-management': 'folder',
  '/settings/import-lists': 'list',
  '/settings/profiles': 'star',
  '/settings/quality': 'gauge',
  '/settings/indexers': 'filter',
  '/settings/downloads': 'download',
  '/settings/usenet': 'server',
  '/settings/connect': 'plug',
  '/settings/metadata': 'filetext',
  '/settings/general': 'gear',
  '/settings/ui': 'palette',
}

const systemIcons: Record<string, IconName> = {
  '/system/status': 'heart',
  '/system/tasks': 'clock',
  '/system/backup': 'archive',
  '/system/logs': 'logs',
}

export const groups: NavGroup[] = [
  {
    key: 'library',
    title: 'Library',
    icon: 'film',
    pages: [
      { to: '/library/movies', label: 'Movies', icon: 'film', count: 'movies', keywords: 'films' },
      { to: '/library/series', label: 'Series', icon: 'tv', count: 'series', keywords: 'shows tv episodes' },
      { to: '/calendar', label: 'Calendar', icon: 'calendar', keywords: 'upcoming ical' },
      { to: '/wanted/missing', label: 'Wanted', icon: 'target', match: ['/wanted'], keywords: 'missing cutoff' },
    ],
  },
  {
    key: 'downloads',
    title: 'Downloads',
    icon: 'download',
    pages: [
      { to: '/activity', label: 'Activity', icon: 'activity', badge: 'downloads', keywords: 'queue history blocklist downloads' },
      { to: '/indexers', label: 'Indexers', icon: 'network', match: ['/indexers'], keywords: 'prowlarr search stats newznab' },
    ],
  },
  {
    key: 'tools',
    title: 'Tools',
    icon: 'wrench',
    pages: [
      { to: '/tools/renamer', label: 'File renamer', icon: 'rename', keywords: 'rename bulk regex' },
      { to: '/settings/release-test', label: 'Release test', icon: 'sparkle', keywords: 'parse release name' },
    ],
  },
  {
    key: 'settings',
    title: 'Settings',
    icon: 'sliders',
    foldable: true,
    pages: settingsAreas
      .filter((a) => a.to !== '/settings/release-test')
      .map((a) => ({ to: a.to, label: a.label, icon: settingsIcons[a.to] ?? 'sliders', keywords: a.description })),
  },
  {
    key: 'system',
    title: 'System',
    icon: 'gauge',
    foldable: true,
    pages: systemAreas.map((a) => ({ to: a.to, label: a.label, icon: systemIcons[a.to] ?? 'gauge', keywords: a.description })),
  },
]

/** Sub-pages shown as tabs below the top bar. */
export const hubs: { prefix: string; tabs: { to: string; label: string; icon: IconName }[] }[] = [
  {
    prefix: '/wanted',
    tabs: [
      { to: '/wanted/missing', label: 'Missing', icon: 'target' },
      { to: '/wanted/cutoff', label: 'Cutoff unmet', icon: 'arrowup' },
    ],
  },
  {
    prefix: '/indexers',
    tabs: [
      { to: '/indexers', label: 'Indexers', icon: 'network' },
      { to: '/indexers/search', label: 'Search', icon: 'search' },
      { to: '/indexers/stats', label: 'Stats', icon: 'gauge' },
      { to: '/indexers/history', label: 'History', icon: 'history' },
    ],
  },
]

const allPages = [overview, ...groups.flatMap((g) => g.pages)]

export const isActive = (page: NavPage, pathname: string) =>
  page.to === '/'
    ? pathname === '/'
    : pathname === page.to || pathname.startsWith(`${page.to}/`) || (page.match ?? []).some((m) => pathname === m || pathname.startsWith(`${m}/`))

/** The page title for the top bar. */
export function titleFor(pathname: string): string {
  if (pathname === '/settings') return 'Settings'
  if (pathname === '/library/import') return 'Import library'
  if (pathname.startsWith('/library/movies/')) return 'Movies'
  if (pathname.startsWith('/library/series/')) return 'Series'
  for (const hub of hubs) {
    const tab = hub.tabs.find((t) => t.to === pathname)
    if (tab && hub.prefix !== tab.to) return `${allPages.find((p) => isActive(p, pathname))?.label ?? ''} · ${tab.label}`
  }
  return allPages.find((p) => isActive(p, pathname))?.label ?? 'StreamZH'
}

export const paletteTargets = [
  ...allPages.map((p) => ({ to: p.to, label: p.label, icon: p.icon, keywords: p.keywords ?? '', group: groups.find((g) => g.pages.includes(p))?.title ?? '' })),
  ...hubs.flatMap((h) =>
    h.tabs.filter((t) => !allPages.some((p) => p.to === t.to)).map((t) => ({ to: t.to, label: t.label, icon: t.icon, keywords: '', group: titleFor(h.prefix) })),
  ),
  { to: '/settings', label: 'All settings', icon: 'sliders' as IconName, keywords: 'overview', group: 'Settings' },
  { to: '/library/import', label: 'Import existing library', icon: 'folderplus' as IconName, keywords: 'add folder scan', group: 'Library' },
]
