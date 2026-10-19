import { useQueryClient, type UseQueryResult } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { initials, nameOf, roleNames, usageText, usersApi, usersKeys, type Role, type UserAccount, type UserInput } from '../api/users'
import { meKeys } from '../api/me'
import { useAuth } from '../auth/auth'
import { useToasts } from '../activity/hooks'
import Icon from '../components/Icon'
import { Skeleton } from '../components/Loader'
import ui from '../components/ui.module.css'
import Toasts from '../indexerTools/Toast'
import { formatDateTime, relativeTime } from '../lib/format'
import { ConfirmDialog, PasswordDialog, UserDialog } from './UserDialogs'
import styles from './Users.module.css'

type Filter = 'all' | 'admins' | 'friends' | 'requesters'

const filters: { key: Filter; label: string; roles: Role[] }[] = [
  { key: 'all', label: 'All', roles: ['owner', 'admin', 'friend', 'requester'] },
  { key: 'admins', label: 'Admins', roles: ['owner', 'admin'] },
  { key: 'friends', label: 'Friends', roles: ['friend'] },
  { key: 'requesters', label: 'Requesters', roles: ['requester'] },
]

type Open =
  | { kind: 'edit'; user: UserAccount }
  | { kind: 'password'; user: UserAccount }
  | { kind: 'sessions'; user: UserAccount }
  | { kind: 'unlink'; user: UserAccount }
  | { kind: 'delete'; user: UserAccount }

export function RoleChip({ role }: { role: Role }) {
  return <span className={`${styles.role} ${styles[`role_${role}`]}`}>{roleNames[role]}</span>
}

/** The accounts: search, role filter, table with role, request usage, Discord link and last sign-in. */
export default function UsersTab({
  query,
  adding,
  onAddingDone,
}: {
  query: UseQueryResult<UserAccount[]>
  adding: boolean
  onAddingDone: () => void
}) {
  const queryClient = useQueryClient()
  const { user: me } = useAuth()
  const isOwner = me?.role === 'Owner'
  const [search, setSearch] = useState('')
  const [filter, setFilter] = useState<Filter>('all')
  const [open, setOpen] = useState<Open | null>(null)
  const { toasts, push, dismiss } = useToasts()

  const all = useMemo(() => query.data ?? [], [query.data])
  const list = useMemo(() => {
    const roles = filters.find((f) => f.key === filter)!.roles
    const q = search.trim().toLowerCase()
    return all.filter(
      (u) =>
        roles.includes(u.role) &&
        (!q || [u.username, u.displayName ?? '', u.email ?? '', u.discord?.name ?? ''].some((s) => s.toLowerCase().includes(q))),
    )
  }, [all, filter, search])

  const refresh = () => Promise.all([queryClient.invalidateQueries({ queryKey: usersKeys.all }), queryClient.invalidateQueries({ queryKey: meKeys.me })])
  // Admins cannot change owners; nobody deletes themselves.
  const canManage = (u: UserAccount) => isOwner || u.role !== 'owner'

  const save = async (user: UserAccount | null, input: UserInput) => {
    if (user) {
      await usersApi.update(user.id, input)
      push('ok', `${nameOf({ displayName: input.displayName, username: user.username })} saved`)
    } else {
      await usersApi.create(input)
      push('ok', `${input.username} added`)
    }
    await refresh()
  }

  return (
    <>
      <div className={styles.toolbar}>
        <label className={styles.search}>
          <Icon name="search" size={16} />
          <input
            className={ui.input}
            type="search"
            placeholder="Name, e-mail or Discord"
            aria-label="Search users"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            spellCheck={false}
          />
        </label>
        <div className={ui.seg} role="group" aria-label="Filter by role">
          {filters.map((f) => (
            <button key={f.key} type="button" aria-pressed={filter === f.key} onClick={() => setFilter(f.key)}>
              {f.label}
              <span className={`${ui.num} ${ui.muted}`}>{all.filter((u) => f.roles.includes(u.role)).length}</span>
            </button>
          ))}
        </div>
      </div>

      {query.isError && <p className={ui.errBox}>{query.error.message}</p>}

      <div className={`${ui.panel} ${styles.panel}`}>
        {query.isPending ? (
          <div className={styles.empty}>
            <Skeleton rows={4} />
          </div>
        ) : list.length === 0 ? (
          <div className={`${ui.empty} ${styles.empty}`}>{all.length > 1 || search ? 'No user matches.' : 'Only you so far. Add a user or create an invite link.'}</div>
        ) : (
          <div className={styles.tableWrap}>
            <table className={`${ui.table} ${styles.table}`}>
              <thead>
                <tr>
                  <th>User</th>
                  <th>Role</th>
                  <th className={styles.hideSm}>Requests</th>
                  <th className={styles.hideMd}>Discord</th>
                  <th className={styles.hideSm}>Last sign-in</th>
                  <th className={styles.hideSm} aria-label="Actions" />
                </tr>
              </thead>
              <tbody>
                {list.map((u, i) => {
                  const self = u.id === me?.id
                  const manage = canManage(u)
                  return (
                    <tr
                      key={u.id}
                      className={`${styles.row} ${u.enabled ? '' : styles.dim}`}
                      style={{ animationDelay: `${Math.min(i, 12) * 25}ms` }}
                      onClick={() => manage && setOpen({ kind: 'edit', user: u })}
                    >
                      <td>
                        <div className={styles.cellUser}>
                          <span className={styles.avatar} aria-hidden="true">
                            {initials(u)}
                          </span>
                          <div style={{ minWidth: 0 }}>
                            <b>{nameOf(u)}</b>
                            {self && <span className={styles.you}>you</span>}
                            <small>
                              @{u.username}
                              {u.email ? ` · ${u.email}` : ''}
                            </small>
                          </div>
                        </div>
                      </td>
                      <td>
                        <RoleChip role={u.role} />
                        {!u.enabled && (
                          <div className={styles.stateLine}>
                            <span className={`${ui.pill} ${ui.pillErr}`}>Disabled</span>
                          </div>
                        )}
                      </td>
                      <td className={styles.hideSm}>
                        <div className={styles.usage} title={`Within ${u.usage.movies.days} days; ${u.requestCount} requests in total`}>
                          <div>
                            {usageText(u.usage.movies)} <span>movies</span> · {usageText(u.usage.series)} <span>series</span>
                          </div>
                          <span>
                            {u.effectiveAutoApprove ? 'Approved automatically' : 'Needs approval'}
                            {u.customLimits ? ' · own limits' : ''}
                          </span>
                        </div>
                      </td>
                      <td className={styles.hideMd}>
                        {u.discord ? (
                          <span className={styles.discord} title={`Discord id ${u.discord.userId}`}>
                            <Icon name="chat" size={14} />
                            {u.discord.name ?? u.discord.userId}
                          </span>
                        ) : (
                          <span className={ui.muted}>–</span>
                        )}
                      </td>
                      <td className={`${styles.hideSm} ${styles.when}`} title={u.lastLogin ? formatDateTime(u.lastLogin) : undefined}>
                        {u.lastLogin ? relativeTime(u.lastLogin) : <span className={ui.muted}>Never</span>}
                      </td>
                      <td className={`${styles.actionsCell} ${styles.hideSm}`} onClick={(e) => e.stopPropagation()}>
                        {manage ? (
                          <div className={styles.actions}>
                            <button type="button" className={`${ui.ghost} ${ui.icon} ${ui.sm}`} title="Edit" aria-label={`Edit ${u.username}`} onClick={() => setOpen({ kind: 'edit', user: u })}>
                              <Icon name="edit" size={15} />
                            </button>
                            {/* Your own password: under Profile, with the current one (0.12). */}
                            {!self && (
                              <button
                                type="button"
                                className={`${ui.ghost} ${ui.icon} ${ui.sm}`}
                                title="Reset password"
                                aria-label={`Reset the password of ${u.username}`}
                                onClick={() => setOpen({ kind: 'password', user: u })}
                              >
                                <Icon name="key" size={15} />
                              </button>
                            )}
                            <button
                              type="button"
                              className={`${ui.ghost} ${ui.icon} ${ui.sm}`}
                              title="Sign out everywhere"
                              aria-label={`Sign ${u.username} out everywhere`}
                              onClick={() => setOpen({ kind: 'sessions', user: u })}
                            >
                              <Icon name="logout" size={15} />
                            </button>
                            {u.discord && (
                              <button
                                type="button"
                                className={`${ui.ghost} ${ui.icon} ${ui.sm}`}
                                title="Unlink Discord"
                                aria-label={`Unlink the Discord account of ${u.username}`}
                                onClick={() => setOpen({ kind: 'unlink', user: u })}
                              >
                                <Icon name="link" size={15} />
                              </button>
                            )}
                            {!self && (
                              <button
                                type="button"
                                className={`${ui.ghost} ${ui.icon} ${ui.sm}`}
                                title="Delete"
                                aria-label={`Delete ${u.username}`}
                                onClick={() => setOpen({ kind: 'delete', user: u })}
                              >
                                <Icon name="trash" size={15} />
                              </button>
                            )}
                          </div>
                        ) : (
                          <span className={ui.muted} title="Only an owner can change an owner.">
                            <Icon name="lock" size={15} />
                          </span>
                        )}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {(adding || open?.kind === 'edit') && (
        <UserDialog
          user={open?.kind === 'edit' ? open.user : null}
          isOwner={isOwner}
          self={open?.kind === 'edit' && open.user.id === me?.id}
          actions={open?.kind === 'edit' ? <EditActions user={open.user} self={open.user.id === me?.id} onOpen={setOpen} /> : undefined}
          onSave={(input) => save(open?.kind === 'edit' ? open.user : null, input)}
          onClose={() => (setOpen(null), onAddingDone())}
        />
      )}
      {open?.kind === 'password' && (
        <PasswordDialog
          user={open.user}
          onSave={async (password) => {
            await usersApi.resetPassword(open.user.id, password)
            push('ok', 'Password set', `${nameOf(open.user)} was signed out everywhere.`)
          }}
          onClose={() => setOpen(null)}
        />
      )}
      {open?.kind === 'sessions' && (
        <ConfirmDialog
          title="Sign out everywhere"
          confirm="Sign out"
          onConfirm={async () => {
            await usersApi.revokeSessions(open.user.id)
            push('ok', `${nameOf(open.user)} was signed out everywhere`)
          }}
          onClose={() => setOpen(null)}
        >
          Ends every session of <b>{nameOf(open.user)}</b> on all devices.
          {open.user.id === me?.id && ' This browser stays signed in.'}
        </ConfirmDialog>
      )}
      {open?.kind === 'unlink' && (
        <ConfirmDialog
          title="Unlink Discord"
          confirm="Unlink"
          onConfirm={async () => {
            await usersApi.unlinkDiscord(open.user.id)
            await refresh()
          }}
          onClose={() => setOpen(null)}
        >
          <b>{nameOf(open.user)}</b> can no longer request through the Discord bot as this user until they link again.
        </ConfirmDialog>
      )}
      {open?.kind === 'delete' && (
        <ConfirmDialog
          title="Delete user"
          confirm="Delete"
          danger
          onConfirm={async () => {
            await usersApi.remove(open.user.id)
            push('ok', `${nameOf(open.user)} deleted`)
            await refresh()
          }}
          onClose={() => setOpen(null)}
        >
          Delete <b>{nameOf(open.user)}</b> (@{open.user.username})?{' '}
          {open.user.requestCount === 0
            ? 'This cannot be undone.'
            : `Their ${open.user.requestCount === 1 ? 'request stays' : `${open.user.requestCount} requests stay`}, without a name. This cannot be undone.`}
        </ConfirmDialog>
      )}
      <Toasts toasts={toasts} onDismiss={dismiss} />
    </>
  )
}

/** Reset password, sign out everywhere and delete inside the edit dialog (the only way on a phone, where the row icons are hidden). */
function EditActions({ user, self, onOpen }: { user: UserAccount; self: boolean; onOpen: (open: Open) => void }) {
  return (
    <>
      {!self && (
        <button type="button" className={`${ui.ghost} ${ui.icon}`} title="Reset password" aria-label="Reset password" onClick={() => onOpen({ kind: 'password', user })}>
          <Icon name="key" size={16} />
        </button>
      )}
      <button type="button" className={`${ui.ghost} ${ui.icon}`} title="Sign out everywhere" aria-label="Sign out everywhere" onClick={() => onOpen({ kind: 'sessions', user })}>
        <Icon name="logout" size={16} />
      </button>
      {!self && (
        <button type="button" className={`${ui.ghost} ${ui.icon}`} title="Delete" aria-label="Delete user" onClick={() => onOpen({ kind: 'delete', user })}>
          <Icon name="trash" size={16} />
        </button>
      )}
    </>
  )
}
