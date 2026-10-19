import { useId } from 'react'
import FormatField from '../../mediaManagement/FormatField'
import { Check } from './RuleForms'
import styles from './Renamer.module.css'
import { simpleEpisodeFormats, simpleMovieFormats, type SimpleSetup } from './rules'

/**
 * Step 2 of the simple renamer: episodes or movies, a pattern to pick (or type), and an example from the preview.
 * Filters, regex and own rules are behind the Advanced switch.
 */
export default function SimpleName({
  setup,
  onChange,
  kindFixed,
  seasonFolder,
  example,
}: {
  setup: SimpleSetup
  onChange: (setup: SimpleSetup) => void
  /** True when a movie or series from the library decides the kind. */
  kindFixed: boolean
  /** The season folder name from the settings, e.g. "Season 1". */
  seasonFolder: string | null
  example: string | null
}) {
  const id = useId()
  const episode = setup.kind === 'episode'
  const formats = episode ? simpleEpisodeFormats : simpleMovieFormats
  const value = episode ? setup.episodeFormat : setup.movieFormat
  const set = (format: string) => onChange(episode ? { ...setup, episodeFormat: format } : { ...setup, movieFormat: format })

  return (
    <>
      {!kindFixed && (
        <div className={styles.segmented} role="tablist" aria-label="Files are">
          <button type="button" role="tab" aria-selected={episode} className={episode ? styles.segmentOn : ''} onClick={() => onChange({ ...setup, kind: 'episode' })}>
            Series episodes
          </button>
          <button type="button" role="tab" aria-selected={!episode} className={!episode ? styles.segmentOn : ''} onClick={() => onChange({ ...setup, kind: 'movie' })}>
            Movies
          </button>
        </div>
      )}

      <div className={styles.chips} role="group" aria-label="Pattern">
        {formats.map((f) => (
          <button key={f.value} type="button" aria-pressed={value === f.value} className={`${styles.chip} ${value === f.value ? styles.chipOn : ''}`} onClick={() => set(f.value)}>
            {f.label}
          </button>
        ))}
      </div>

      <div className={styles.simpleFormat}>
        <label htmlFor={id} className={styles.fieldLabel}>
          Pattern
        </label>
        <FormatField id={id} value={value} onChange={set} kind={episode ? 'episodeFile' : 'movieFile'} />
      </div>

      <p className={styles.simpleExample}>
        <span>Example</span>
        <code>{example ?? (episode ? 'The Series Title - S01E01 - Pilot.mkv' : 'Interstellar (2014).mkv')}</code>
      </p>

      {episode && (
        <div className={styles.checks}>
          <Check checked={setup.seasonFolders} onChange={(seasonFolders) => onChange({ ...setup, seasonFolders })}>
            Sort into season folders{seasonFolder ? ` (${seasonFolder})` : ''}
          </Check>
        </div>
      )}

      <p className={styles.simpleHint}>Titles come from your library, otherwise from TMDB. Subtitles follow their video. Filters, regular expressions and own rules: turn on Advanced.</p>
    </>
  )
}
