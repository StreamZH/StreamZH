import { useMutation } from '@tanstack/react-query'
import { useId } from 'react'
import { libraryApi } from '../api/library'
import { metadataApi, metadataKeys, type MetadataSettings } from '../api/metadata'
import ui from '../components/ui.module.css'
import { Row, SaveBar, Section, Select, Toggle } from './SettingsKit'
import { useSettingsForm } from './useSettingsForm'
import styles from './Metadata.module.css'

type MetadataDraft = MetadataSettings & { tmdbToken: string }

const countryNames: Record<string, string> = {
  US: 'United States',
  GB: 'United Kingdom',
  DE: 'Germany',
  AT: 'Austria',
  CH: 'Switzerland',
  FR: 'France',
  IT: 'Italy',
  ES: 'Spain',
  NL: 'Netherlands',
  BE: 'Belgium',
  DK: 'Denmark',
  SE: 'Sweden',
  NO: 'Norway',
  FI: 'Finland',
  PT: 'Portugal',
  BR: 'Brazil',
  CA: 'Canada',
  AU: 'Australia',
  NZ: 'New Zealand',
  IE: 'Ireland',
  JP: 'Japan',
  KR: 'South Korea',
}

const withToken = (s: MetadataSettings): MetadataDraft => ({ ...s, tmdbToken: '' })

/** Settings → Metadata → TMDB: token, language, certification country, NFO title and the image cache. */
export function MetadataSection() {
  const id = useId()
  const form = useSettingsForm<MetadataDraft>({
    queryKey: [...metadataKeys.settings, 'form'],
    load: () => metadataApi.settings().then(withToken),
    save: (d) =>
      metadataApi
        .save({
          tmdbToken: d.tmdbToken.trim() || null,
          language: d.language,
          certificationCountry: d.certificationCountry,
          nfoOriginalTitle: d.nfoOriginalTitle,
          imageCache: d.imageCache,
        })
        .then(withToken),
    invalidate: [metadataKeys.settings],
  })
  const test = useMutation({ mutationFn: () => metadataApi.test(form.values?.tmdbToken.trim() || null) })
  const refreshAll = useMutation({ mutationFn: libraryApi.refreshAll })

  const v = form.values
  if (!v) return form.query.error ? <p className={ui.error}>{form.query.error.message}</p> : null

  return (
    <Section
      id="tmdb"
      title="TMDB"
      description="StreamZH finds titles, posters and episode lists on The Movie Database."
      hiddenCount={2}
      footer={<SaveBar dirty={form.dirty} saving={form.saving} saved={form.saved} error={form.formError} onSave={form.save} onReset={form.reset} />}
    >
      <Row
        label="API token"
        htmlFor={`${id}-token`}
        error={form.error('tmdbToken')}
        help={
          <>
            Create a free account on{' '}
            <a href="https://www.themoviedb.org/settings/api" target="_blank" rel="noreferrer">
              themoviedb.org
            </a>{' '}
            and paste the <em>API Read Access Token</em> (or the shorter API key).
          </>
        }
      >
        <div className={styles.tokenLine}>
          <input
            id={`${id}-token`}
            className={`${ui.input} ${ui.mono} ${styles.token}`}
            type="password"
            autoComplete="off"
            placeholder={v.tmdbConfigured ? 'Leave empty to keep the saved token' : 'eyJhbGciOi…'}
            value={v.tmdbToken}
            onChange={(e) => form.set({ tmdbToken: e.target.value })}
          />
          {v.tmdbConfigured ? <span className={`${ui.badge} ${ui.ok}`}>Saved {v.tmdbTokenHint}</span> : <span className={`${ui.badge} ${ui.missing}`}>Not set</span>}
        </div>
        <div className={styles.actions}>
          <button className={ui.button} type="button" onClick={() => test.mutate()} disabled={test.isPending || (!v.tmdbToken.trim() && !v.tmdbConfigured)}>
            {test.isPending ? 'Testing…' : 'Test connection'}
          </button>
          <span aria-live="polite">
            {test.isSuccess && <span className={ui.ok}>TMDB connection works</span>}
            {test.error && <span className={ui.error}>{test.error.message}</span>}
          </span>
        </div>
      </Row>

      <Row
        label="Language"
        htmlFor={`${id}-language`}
        error={form.error('language')}
        help="For titles and descriptions. Swiss and Austrian variants use the German titles, since TMDB has few regional translations. Changing it loads the whole library again."
      >
        <Select id={`${id}-language`} value={v.language} onChange={(language) => form.set({ language })} options={v.languages.map((l) => ({ value: l, label: l }))} />
      </Row>

      <Row
        label="Certification country"
        htmlFor={`${id}-country`}
        error={form.error('certificationCountry')}
        help="Whose age ratings are used: the rating in NFO files and the {Movie Certification} naming token."
      >
        <Select
          id={`${id}-country`}
          value={v.certificationCountry}
          onChange={(certificationCountry) => form.set({ certificationCountry })}
          options={v.certificationCountries.map((c) => ({ value: c, label: `${countryNames[c] ?? c} (${c})` }))}
        />
      </Row>

      <Row label="Original title in NFO" advanced error={form.error('nfoOriginalTitle')} help="NFO files get the original title (e.g. «Le Fabuleux Destin d'Amélie Poulain») instead of the translated one.">
        <Toggle checked={v.nfoOriginalTitle} onChange={(nfoOriginalTitle) => form.set({ nfoOriginalTitle })} />
      </Row>

      <Row
        label="Local image cache"
        advanced
        error={form.error('imageCache')}
        help="Posters and backgrounds load through StreamZH and are kept in the data folder (MediaCover), so browsers never contact TMDB. Uses some disk space."
      >
        <Toggle checked={v.imageCache} onChange={(imageCache) => form.set({ imageCache })} />
      </Row>

      <Row label="Refresh" help="Loads titles, posters and episodes again for the whole library. Happens automatically when the language changes.">
        <div className={styles.actions}>
          <button className={ui.button} type="button" onClick={() => refreshAll.mutate()} disabled={refreshAll.isPending || !v.tmdbConfigured}>
            Refresh all metadata
          </button>
          <span aria-live="polite">
            {refreshAll.isSuccess && <span className={ui.ok}>{refreshAll.data.queued} titles queued, progress shows in the status window.</span>}
            {refreshAll.error && <span className={ui.error}>{refreshAll.error.message}</span>}
          </span>
        </div>
      </Row>

      <p className={styles.attribution}>This product uses the TMDB API but is not endorsed or certified by TMDB.</p>
    </Section>
  )
}
