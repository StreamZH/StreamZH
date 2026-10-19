import { useAuth } from '../auth/auth'

/** True for the owner (also the local-network and External sign-in, which act as the owner). */
export function useIsOwner() {
  const { user } = useAuth()
  return user?.role === 'Owner'
}
