import { useQuery } from '@tanstack/react-query'
import { requestKeys, requestsApi } from '../api/requests'
import { useRole } from './roles'

/**
 * Pending requests for the admins' nav badge and the Pending tab. The hub message "requestChanged" refreshes it
 * (useLiveConnection); the 60 s interval covers a missing or broken live connection.
 */
export function usePendingCount(): number {
  const { admin } = useRole()
  const query = useQuery({
    queryKey: requestKeys.count,
    queryFn: requestsApi.pendingCount,
    enabled: admin,
    refetchInterval: 60_000,
    retry: false,
  })
  return admin ? (query.data ?? 0) : 0
}
