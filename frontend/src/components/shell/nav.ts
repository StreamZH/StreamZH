// The navigation of StreamZH, built like CloudZH's: a short sidebar (an overview on top, then a few groups with a title
// and a red icon, pages indented on a guide line), and related pages as tabs in the main window below the top bar
// (hubs, like CloudZH's HUBS). Settings has two rows: its groups, then the pages of the open group.
import { settingsGroups, systemAreas, toolAreas, type SettingsArea } from '../../settings/areas'
import type { IconName } from '../Icon'

export interface NavPage {
  to: string
  label: string
  icon: IconName
  /** Count next to the label (library size) or a red badge (active downloads). */
  count?: 'movies' | 'series'
  badge?: 'downloads' | 'requests'
  /** The page opens a hub: it is active on every tab of that hub. */
  hub?: string
  /** Words the command palette finds this page by. */
  keywords?: string
}

export interface NavGroup {
  key: string
  title: string
  icon: IconName
  pages: NavPage[]
}

/** One tab of a hub; a tab is always its own address. */
export interface HubTab {
  to: string
  label: string
  icon: IconName
  keywords?: string
  /** Top bar and browser title, when "Hub · Tab" does not fit. */
  title?: string
  /** False: the search (Ctrl K) leaves this tab out (the sidebar entry already leads there). */
  palette?: boolean
}

/** A row of tabs; a hub with more than one section shows them as a first row, the open section's tabs below. */
export interface HubSection {
  key: string
  label: string
  icon: IconName
  tabs: HubTab[]
  /** Hidden unless the Advanced switch is on or it is needed right now (see HubTabs). */
  advanced?: boolean
}

export interface Hub {
  key: string
  title: string
  sections: HubSection[]
}

export const overview: NavPage = { to: '/', label: 'Overview', icon: 'home', keywords: 'home dashboard start' }

// 0.10: Discover and Requests, for every role. Requesters see only this group (plus their profile).
const discoverPage: NavPage = { to: '/discover', label: 'Discover', icon: 'compass', keywords: 'request trending popular upcoming find tmdb' }
const requestsPage: NavPage = { to: '/requests', label: 'Requests', icon: 'inbox', badge: 'requests', keywords: 'approve decline pending requested' }

/** The whole navigation of a requester: Discover, Requests and the profile. */
export const requesterGroups: NavGroup[] = [
  {
    key: 'requests',
    title: 'Requests',
    icon: 'compass',
    pages: [discoverPage, requestsPage, { to: '/profile', label: 'Profile', icon: 'user', keywords: 'account password discord' }],
  },
]

export const groups: NavGroup[] = [
  {
    key: 'library',
    title: 'Library',
    icon: 'film',
    pages: [
      { to: '/library/movies', label: 'Movies', icon: 'film', count: 'movies', keywords: 'films' },
      { to: '/library/series', label: 'Series', icon: 'tv', count: 'series', keywords: 'shows tv episodes' },
      { to: '/calendar', label: 'Calendar', icon: 'calendar', keywords: 'upcoming ical' },
      { to: '/wanted/missing', label: 'Wanted', icon: 'target', hub: 'wanted', keywords: 'missing cutoff' },
    ],
  },
  { key: 'requests', title: 'Requests', icon: 'compass', pages: [discoverPage, requestsPage] },
  {
    key: 'downloads',
    title: 'Downloads',
    icon: 'download',
    pages: [
      { to: '/activity', label: 'Activity', icon: 'activity', badge: 'downloads', keywords: 'queue history blocklist downloads' },
      { to: '/indexers', label: 'Indexers', icon: 'network', hub: 'indexers', keywords: 'prowlarr search stats newznab' },
    ],
  },
  {
    key: 'manage',
    title: 'Manage',
    icon: 'sliders',
    pages: [
      { to: '/tools/renamer', label: 'Tools', icon: 'wrench', hub: 'tools', keywords: 'file renamer release test' },
      { to: '/settings', label: 'Settings', icon: 'sliders', hub: 'settings', keywords: 'all settings overview preferences configuration' },
      { to: '/system/status', label: 'System', icon: 'gauge', hub: 'system', keywords: 'status tasks backup logs health' },
    ],
  },
]

const tab = (a: SettingsArea): HubTab => ({ to: a.to, label: a.label, icon: a.icon, keywords: `${a.description} ${a.keywords ?? ''}` })

export const hubs: Hub[] = [
  {
    key: 'wanted',
    title: 'Wanted',
    sections: [
      {
        key: 'wanted',
        label: 'Wanted',
        icon: 'target',
        tabs: [
          { to: '/wanted/missing', label: 'Missing', icon: 'target' },
          { to: '/wanted/cutoff', label: 'Cutoff unmet', icon: 'arrowup', keywords: 'upgrade' },
        ],
      },
    ],
  },
  {
    key: 'indexers',
    title: 'Indexers',
    sections: [
      {
        key: 'indexers',
        label: 'Indexers',
        icon: 'network',
        tabs: [
          { to: '/indexers', label: 'Indexers', icon: 'network', palette: false },
          { to: '/indexers/search', label: 'Search', icon: 'search', keywords: 'manual interactive' },
          { to: '/indexers/stats', label: 'Stats', icon: 'gauge', keywords: 'statistics' },
          { to: '/indexers/history', label: 'History', icon: 'history' },
        ],
      },
    ],
  },
  { key: 'tools', title: 'Tools', sections: [{ key: 'tools', label: 'Tools', icon: 'wrench', tabs: toolAreas.map(tab) }] },
  {
    key: 'settings',
    title: 'Settings',
    sections: [
      { key: 'overview', label: 'Overview', icon: 'grid', tabs: [{ to: '/settings', label: 'Overview', icon: 'grid', title: 'Settings', palette: false }] },
      ...settingsGroups.map((g) => ({ key: g.key, label: g.label, icon: g.icon, advanced: g.advanced, tabs: g.areas.map(tab) })),
    ],
  },
  { key: 'system', title: 'System', sections: [{ key: 'system', label: 'System', icon: 'gauge', tabs: systemAreas.map(tab) }] },
]

/** The page below the tabs (Layout) is their tab panel. */
export const hubPanelId = 'hub-panel'

export const hubTabId = (to: string) => `hubtab${to.replace(/[^a-z0-9]+/gi, '-')}`

/** The hub, section and tab of an address, if it is a tab. */
export function hubOf(pathname: string): { hub: Hub; section: HubSection; tab: HubTab } | null {
  for (const hub of hubs) {
    for (const section of hub.sections) {
      const found = section.tabs.find((t) => t.to === pathname)
      if (found) return { hub, section, tab: found }
    }
  }
  return null
}

export const isActive = (page: NavPage, pathname: string) =>
  page.to === '/'
    ? pathname === '/'
    : page.hub
      ? hubOf(pathname)?.hub.key === page.hub
      : pathname === page.to || pathname.startsWith(`${page.to}/`)

const allPages = [overview, ...groups.flatMap((g) => g.pages)]

/** The page title for the top bar. */
export function titleFor(pathname: string): string {
  if (pathname === '/library/import') return 'Import library'
  if (pathname.startsWith('/library/movies/')) return 'Movies'
  if (pathname.startsWith('/library/series/')) return 'Series'
  if (pathname.startsWith('/discover/')) return 'Discover'
  if (pathname === '/profile') return 'Profile'
  if (pathname === '/get-started') return 'Get started'
  const at = hubOf(pathname)
  if (at) return at.tab.title ?? (at.tab.label === at.hub.title ? at.hub.title : `${at.hub.title} · ${at.tab.label}`)
  return allPages.find((p) => isActive(p, pathname))?.label ?? 'StreamZH'
}

/** Everything the search (Ctrl K) can jump to: every sidebar entry, every tab and a few pages without an entry. */
export const paletteTargets = [
  ...allPages.map((p) => ({ to: p.to, label: p.label, icon: p.icon, keywords: p.keywords ?? '', group: groups.find((g) => g.pages.includes(p))?.title ?? '' })),
  ...hubs.flatMap((h) =>
    h.sections.flatMap((s) =>
      s.tabs
        .filter((t) => t.palette !== false)
        .map((t) => ({ to: t.to, label: t.label, icon: t.icon, keywords: t.keywords ?? '', group: h.sections.length > 1 ? `${h.title} · ${s.label}` : h.title })),
    ),
  ),
  { to: '/profile', label: 'Profile', icon: 'user' as IconName, keywords: 'account password discord link', group: '' },
  { to: '/library/import', label: 'Import existing library', icon: 'folderplus' as IconName, keywords: 'add folder scan', group: 'Library' },
  { to: '/get-started', label: 'Get started', icon: 'sparkle' as IconName, keywords: 'setup guide checklist first steps wizard', group: '' },
]
