import { Link } from 'react-router'
import type { MediaType } from '../api/library'
import page from '../pages/Page.module.css'
import styles from './Library.module.css'

/** Explains what is missing before media can be added. */
export function SetupHint({ tmdbMissing, noRoot, type }: { tmdbMissing: boolean; noRoot: boolean; type: MediaType }) {
  if (!tmdbMissing && !noRoot) return null

  return (
    <div className={`${page.panel} ${styles.setup}`}>
      <h2 className={page.panelTitle}>Two quick steps first</h2>
      <ol>
        <li className={tmdbMissing ? undefined : styles.done}>
          Add your free TMDB API key, so StreamZH can find titles and artwork.
        </li>
        <li className={noRoot ? undefined : styles.done}>
          Add a root folder for {type === 'Movie' ? 'movies' : 'series'}, e.g. <code>{type === 'Movie' ? 'D:\\Movies' : '/media/tv'}</code>.
        </li>
      </ol>
      <Link to="/settings/media-management">Open settings</Link>
    </div>
  )
}
