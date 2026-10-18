import type { Episode } from '../api/library'
import styles from './SeriesType.module.css'

const pad = (n: number, width = 2) => String(n).padStart(width, '0')

/** «Abs.» column head, anime series only. */
export function AbsHeader({ show }: { show: boolean }) {
  if (!show) return null
  return (
    <th className={styles.abs} title="Absolute episode number">
      Abs.
    </th>
  )
}

/** The absolute number of an episode (anime series only); specials have none. */
export function AbsCell({ episode, show }: { episode: Episode; show: boolean }) {
  if (!show) return null
  const absolute = episode.absoluteEpisodeNumber
  return <td className={styles.abs}>{absolute == null ? '' : pad(absolute, 3)}</td>
}

/** «Scene S02E01» when TheXEM numbers the episode differently than TMDB (releases use the scene numbers). */
export function SceneTag({ episode }: { episode: Episode }) {
  const { sceneSeasonNumber: season, sceneEpisodeNumber: number, sceneAbsoluteEpisodeNumber: absolute } = episode
  const differentEpisode = season != null && number != null && (season !== episode.seasonNumber || number !== episode.episodeNumber)
  const differentAbsolute = absolute != null && episode.absoluteEpisodeNumber != null && absolute !== episode.absoluteEpisodeNumber
  if (!differentEpisode && !differentAbsolute) return null

  const parts = [differentEpisode ? `S${pad(season!)}E${pad(number!)}` : null, differentAbsolute ? `#${pad(absolute!, 3)}` : null]
  const text = parts.filter(Boolean).join(' · ')
  return (
    <span className={styles.scene} title="Scene numbering from TheXEM: releases name this episode like that">
      Scene {text}
    </span>
  )
}
