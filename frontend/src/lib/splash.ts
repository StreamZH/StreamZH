// The splash screen from index.html (#splash) and the thin progress bar at the top (#topbar), like CloudZH.

let splashGone = false

/**
 * Hides the splash screen once StreamZH knows what to show. The sequence (monogram, wordmark, progress bar) always
 * runs to the end, about 2.8 s after the page load; with «reduce motion» there is nothing to wait for.
 */
export function splashDone() {
  if (splashGone) return
  splashGone = true
  const splash = document.getElementById('splash')
  if (!splash) return
  const calm = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches
  window.setTimeout(
    () => {
      splash.classList.add('out')
      splash.setAttribute('aria-hidden', 'true')
      window.setTimeout(() => splash.remove(), 620)
    },
    calm ? 0 : Math.max(0, 2800 - performance.now()),
  )
}

let run = 0

/** Starts the bar at the top; returns a token for done(). */
export function progressStart(): number {
  const bar = document.getElementById('topbar')
  const token = ++run
  if (bar) {
    bar.className = 'topbar'
    void bar.offsetWidth
    bar.className = 'topbar run'
  }
  return token
}

/** Finishes the bar (unless a newer page change started in the meantime). */
export function progressDone(token: number) {
  if (token !== run) return
  const bar = document.getElementById('topbar')
  if (bar?.classList.contains('run')) bar.className = 'topbar end'
}
