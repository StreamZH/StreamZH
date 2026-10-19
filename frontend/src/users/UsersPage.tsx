import { useQuery } from '@tanstack/react-query'
import { useRef, useState, type KeyboardEvent } from 'react'
import { useSearchParams } from 'react-router'
import { usersApi, usersKeys } from '../api/users'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import { SettingsPage } from '../settings/SettingsKit'
import InvitesTab from './InvitesTab'
import UsersTab from './UsersTab'
import styles from './Users.module.css'

const tabs = [
  { key: 'users', label: 'Users', icon: 'users' },
  { key: 'invites', label: 'Invites', icon: 'send' },
] as const

type TabKey = (typeof tabs)[number]['key']

/** Settings → Users: accounts and roles, and invite links for friends and requesters (0.10). */
export default function UsersPage() {
  const [url, setUrl] = useSearchParams()
  const tab: TabKey = url.get('tab') === 'invites' ? 'invites' : 'users'
  const users = useQuery({ queryKey: usersKeys.all, queryFn: usersApi.list })
  const invites = useQuery({ queryKey: usersKeys.invites, queryFn: usersApi.invites })
  const refs = useRef<Record<TabKey, HTMLButtonElement | null>>({ users: null, invites: null })
  // The "Add user" / "Create invite" button sits in the page head; the open tab shows the dialog.
  const [adding, setAdding] = useState<TabKey | null>(null)

  const select = (key: TabKey) => setUrl(key === 'users' ? {} : { tab: key })
  const onKeyDown = (e: KeyboardEvent) => {
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return
    e.preventDefault()
    const next = tab === 'users' ? 'invites' : 'users'
    select(next)
    refs.current[next]?.focus()
  }

  const counts: Record<TabKey, number | undefined> = {
    users: users.data?.length,
    invites: invites.data?.records.filter((i) => i.state === 'active').length,
  }

  const lead =
    tab === 'users'
      ? 'Who can sign in, what they may do, and how their requests are handled.'
      : 'Links that let someone create their own account with a set role.'

  return (
    <SettingsPage
      title="Users"
      lead={lead}
      advancedToggle={false}
      actions={
        tab === 'users' ? (
          <button type="button" className={ui.primary} onClick={() => setAdding('users')}>
            <Icon name="plus" size={15} /> Add user
          </button>
        ) : (
          <button type="button" className={ui.primary} onClick={() => setAdding('invites')}>
            <Icon name="send" size={15} /> Create invite
          </button>
        )
      }
    >
      <div className={styles.tabs} role="tablist" aria-label="Users" onKeyDown={onKeyDown}>
        {tabs.map((t) => (
          <button
            key={t.key}
            ref={(el) => {
              refs.current[t.key] = el
            }}
            type="button"
            role="tab"
            id={`users-tab-${t.key}`}
            aria-selected={tab === t.key}
            aria-controls={`users-panel-${t.key}`}
            tabIndex={tab === t.key ? 0 : -1}
            className={styles.tab}
            onClick={() => select(t.key)}
          >
            <Icon name={t.icon} size={15} />
            {t.label}
            {counts[t.key] != null && <span className={styles.tcount}>{counts[t.key]}</span>}
          </button>
        ))}
      </div>

      <div role="tabpanel" id={`users-panel-${tab}`} aria-labelledby={`users-tab-${tab}`}>
        {tab === 'users' ? (
          <UsersTab query={users} adding={adding === 'users'} onAddingDone={() => setAdding(null)} />
        ) : (
          <InvitesTab query={invites} adding={adding === 'invites'} onAddingDone={() => setAdding(null)} />
        )}
      </div>
    </SettingsPage>
  )
}
