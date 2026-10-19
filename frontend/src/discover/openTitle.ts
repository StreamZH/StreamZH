import { useLocation, useNavigate, useSearchParams } from 'react-router'
import type { RequestMediaType } from '../api/requests'

export interface OpenTitle {
  mediaType: RequestMediaType
  tmdbId: number
}

/**
 * The open title lives in the URL (?open=movie-603): Back closes the drawer (on a phone it is a full page) and a link
 * opens it directly. Opening pushes a history entry; closing goes back to it when it was ours, otherwise replaces.
 */
export function useOpenTitle(): [OpenTitle | null, (item: OpenTitle | null) => void] {
  const [params, setParams] = useSearchParams()
  const location = useLocation()
  const navigate = useNavigate()
  const match = /^(movie|series)-(\d+)$/.exec(params.get('open') ?? '')
  const open = match ? { mediaType: match[1] as RequestMediaType, tmdbId: Number(match[2]) } : null
  const set = (item: OpenTitle | null) => {
    if (!item && (location.state as { drawer?: boolean } | null)?.drawer) {
      void navigate(-1)
      return
    }
    const next = new URLSearchParams(params)
    if (item) next.set('open', `${item.mediaType}-${item.tmdbId}`)
    else next.delete('open')
    setParams(next, { replace: !item || !!open, preventScrollReset: true, state: item ? { drawer: !open || (location.state as { drawer?: boolean } | null)?.drawer } : null })
  }
  return [open, set]
}
