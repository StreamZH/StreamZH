import { useAuth } from '../auth/auth'

// Who may do what in the request flow, from the session's role (/api/v1/auth/state).

export function useRole() {
  const { user } = useAuth()
  const role = user?.role
  const admin = role === 'Owner' || role === 'Admin'
  return {
    userId: user?.id ?? null,
    admin,
    /** Owner, Admin and Friend: the whole app. */
    member: admin || role === 'Friend',
    /** Discover, Requests and the profile only. */
    requester: role === 'Requester',
  }
}

/** Pages a requester may open; everything else sends them to Discover. */
export const requesterPaths = ['/discover', '/requests', '/profile']

export const requesterMayOpen = (pathname: string) => requesterPaths.some((p) => pathname === p || pathname.startsWith(`${p}/`))
