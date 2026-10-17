import type { IndexerPrivacy, IndexerProtocol } from '../api/indexers'

export const protocolLabels: Record<IndexerProtocol, string> = { usenet: 'Usenet', torrent: 'Torrent' }
export const privacyLabels: Record<IndexerPrivacy, string> = { private: 'Private', semiPrivate: 'Semi-private', public: 'Public' }

let languageNames: Intl.DisplayNames | null = null

/** "en-US" → "English (United States)"; falls back to the code. */
export function languageName(code: string): string {
  if (!code) return '–'
  try {
    languageNames ??= new Intl.DisplayNames(['en'], { type: 'language' })
    return languageNames.of(code) ?? code
  } catch {
    return code
  }
}

/** Top-level category of any Newznab id (5040 → 5000). */
export function topCategoryOf(id: number): number {
  return Math.floor(id / 1000) * 1000
}
