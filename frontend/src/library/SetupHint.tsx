import { Link } from 'react-router'
import type { MediaType } from '../api/library'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import styles from './Library.module.css'

/** Explains what is missing before media can be added. */
export function SetupHint({ tmdbMissing, noRoot, type }: { tmdbMissing: boolean; noRoot: boolean; type: MediaType }) {
  if (!tmdbMissing && !noRoot) return null

  return (
    <section className={`${ui.panel} ${styles.setup}`}>
      <div className={ui.panelHead}>
        <h2 className={styles.panelTitle}>
          <Icon name="sparkle" size={17} />
          Two quick steps first
        </h2>
      </div>
      <ol>
        <li className={tmdbMissing ? undefined : styles.done}>
          Add your free TMDB API key, so StreamZH can find titles and artwork.
        </li>
        <li className={noRoot ? undefined : styles.done}>
          Add a root folder for {type === 'Movie' ? 'movies' : 'series'}, e.g. <code>{type === 'Movie' ? 'D:\\Movies' : '/media/tv'}</code>.
        </li>
      </ol>
      <Link className={`${ui.primary} ${ui.sm}`} to="/settings/media-management">
        Open settings
        <Icon name="right" size={14} />
      </Link>
    </section>
  )
}
