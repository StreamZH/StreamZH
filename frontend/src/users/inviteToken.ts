import { urlBase } from '../lib/urlBase'

/** The token when the address is /invite/<token> (below the URL base), else null. */
export function inviteToken(pathname: string = window.location.pathname): string | null {
  const path = urlBase && pathname.startsWith(urlBase) ? pathname.slice(urlBase.length) : pathname
  const match = /^\/invite\/([A-Za-z0-9_-]{1,100})\/?$/.exec(path)
  return match ? match[1] : null
}
