// The URL base (Settings → General → Host), e.g. "/streamzh" behind a reverse proxy, or "" at the root.
// The server puts it into index.html as <base href="/streamzh/">; the Vite dev server has no base tag.

function readUrlBase(): string {
  const href = document.querySelector('base')?.getAttribute('href')
  if (!href) return ''
  try {
    return new URL(href, window.location.origin).pathname.replace(/\/+$/, '')
  } catch {
    return ''
  }
}

export const urlBase = readUrlBase()

/** Absolute path below the URL base: withBase('/api/v1/x') → '/streamzh/api/v1/x'. */
export function withBase(path: string): string {
  return `${urlBase}${path.startsWith('/') ? path : `/${path}`}`
}
