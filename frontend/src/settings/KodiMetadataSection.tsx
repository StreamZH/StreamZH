import { useMutation } from '@tanstack/react-query'
import { metadataApi, metadataKeys, type KodiMetadataOptions } from '../api/metadata'
import ui from '../components/ui.module.css'
import { Row, SaveBar, Section, Toggle } from './SettingsKit'
import { useSettingsForm } from './useSettingsForm'
import styles from './Metadata.module.css'

type Option = { key: keyof KodiMetadataOptions; label: string; help: string; advanced?: boolean }

const movieOptions: Option[] = [
  { key: 'movieMetadata', label: 'Movie metadata', help: 'An NFO file with title, plot, rating, age rating, genres, cast and crew, named like the video file.' },
  {
    key: 'movieMetadataUrl',
    label: 'Movie metadata URL',
    help: 'Adds the TMDB and IMDb links to the NFO. On its own (movie metadata off), the NFO holds only the links, so Kodi looks the movie up itself.',
    advanced: true,
  },
  { key: 'movieImages', label: 'Movie images', help: 'poster.jpg and fanart.jpg in the movie folder.' },
  { key: 'useMovieNfo', label: 'Use movie.nfo', help: 'Name the NFO movie.nfo instead of after the video file.', advanced: true },
  { key: 'movieCollection', label: 'Collection name', help: 'Write the TMDB collection (e.g. «Dune Collection») as a Kodi movie set.', advanced: true },
]

const seriesOptions: Option[] = [
  { key: 'seriesMetadata', label: 'Series metadata', help: 'tvshow.nfo in the series folder.' },
  { key: 'seriesMetadataUrl', label: 'Series metadata URL', help: 'Adds the TMDB and TVDB links to tvshow.nfo.', advanced: true },
  { key: 'episodeMetadata', label: 'Episode metadata', help: 'An NFO file next to every episode file.' },
  { key: 'seriesImages', label: 'Series images', help: 'poster.jpg and fanart.jpg in the series folder. TMDB has no banners.' },
  { key: 'seasonImages', label: 'Season images', help: 'season01-poster.jpg and so on in the series folder.' },
  { key: 'episodeImages', label: 'Episode images', help: 'The episode still as <episode file>-thumb.jpg.' },
]

/** Settings → Metadata → Kodi (XBMC) / Emby: NFO files and artwork next to the media, like Radarr's and Sonarr's metadata consumer. */
export function KodiMetadataSection() {
  const form = useSettingsForm<KodiMetadataOptions>({ queryKey: metadataKeys.kodi, load: metadataApi.kodi, save: metadataApi.saveKodi })
  const write = useMutation({ mutationFn: metadataApi.writeLibrary })

  const v = form.values
  if (!v) return form.query.error ? <p className={ui.error}>{form.query.error.message}</p> : null

  const savedEnabled = form.query.data?.enabled === true
  const row = (o: Option) => (
    <Row key={o.key} label={o.label} advanced={o.advanced} help={o.help} error={form.error(o.key)}>
      <Toggle checked={v[o.key]} disabled={!v.enabled} onChange={(checked) => form.set({ [o.key]: checked } as Partial<KodiMetadataOptions>)} />
    </Row>
  )

  return (
    <Section
      id="kodi"
      title="Kodi (XBMC) / Emby"
      description="NFO files and artwork next to your media, written after every import and rename. Kodi, Jellyfin and Emby read them; Plex with an NFO agent."
      hiddenCount={[...movieOptions, ...seriesOptions].filter((o) => o.advanced).length}
      footer={<SaveBar dirty={form.dirty} saving={form.saving} saved={form.saved} error={form.formError} onSave={form.save} onReset={form.reset} />}
    >
      <Row label="Enable" help="Off: StreamZH writes no NFO files or images (default).">
        <Toggle checked={v.enabled} onChange={(enabled) => form.set({ enabled })} on="On" off="Off" />
      </Row>
      <h3 className={styles.group}>Movies</h3>
      {movieOptions.map(row)}
      <h3 className={styles.group}>Series</h3>
      {seriesOptions.map(row)}
      <Row
        label="Whole library"
        help="Writes the files for every movie and series now, e.g. after turning this on. Existing images are kept; NFO files are rewritten."
      >
        <div className={styles.actions}>
          <button className={ui.button} type="button" onClick={() => write.mutate()} disabled={!savedEnabled || form.dirty || write.isPending}>
            Write metadata now
          </button>
          <span aria-live="polite">
            {!savedEnabled && <span className={ui.muted}>Turn it on and save first.</span>}
            {write.isSuccess && <span className={ui.ok}>Started, progress shows in the status window.</span>}
            {write.error && <span className={ui.error}>{write.error.message}</span>}
          </span>
        </div>
      </Row>
    </Section>
  )
}
