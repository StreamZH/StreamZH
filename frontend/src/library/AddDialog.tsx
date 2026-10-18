import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { indexerOptionsApi, indexerOptionsKeys } from '../api/indexerOptions'
import { libraryApi, queryKeys, type LookupResult, type MediaType } from '../api/library'
import type { SeriesType } from '../api/seriesType'
import Icon from '../components/Icon'
import { Skeleton } from '../components/Loader'
import Modal from '../components/Modal'
import Poster from '../components/Poster'
import ProfileSelect from '../components/ProfileSelect'
import ui from '../components/ui.module.css'
import styles from './AddDialog.module.css'
import { SeriesTypeField } from './SeriesTypeField'

export default function AddDialog({ type, onClose }: { type: MediaType; onClose: () => void }) {
  const [term, setTerm] = useState('')
  const [debounced, setDebounced] = useState('')
  const [selected, setSelected] = useState<LookupResult | null>(null)

  useEffect(() => {
    const timer = setTimeout(() => setDebounced(term.trim()), 350)
    return () => clearTimeout(timer)
  }, [term])

  const results = useQuery({
    queryKey: ['lookup', type, debounced],
    queryFn: () => libraryApi.lookup(type, debounced),
    enabled: debounced.length >= 2,
    staleTime: 60_000,
  })

  const path = type === 'Movie' ? 'movies' : 'series'

  return (
    <Modal title={type === 'Movie' ? 'Add movie' : 'Add series'} onClose={onClose}>
      <label className={styles.search}>
        <Icon name="search" size={16} />
        <input
          className={ui.input}
          type="search"
          autoFocus
          aria-label="Search TMDB"
          placeholder={type === 'Movie' ? 'Search TMDB, e.g. Dune' : 'Search TMDB, e.g. Breaking Bad'}
          value={term}
          onChange={(e) => {
            setTerm(e.target.value)
            setSelected(null)
          }}
        />
      </label>

      {results.isFetching && !results.data && <Skeleton rows={3} />}
      {results.isError && <p className={ui.error}>{results.error.message}</p>}
      {results.data && results.data.length === 0 && <p className={ui.muted}>No results on TMDB.</p>}

      {(results.data ?? []).length > 0 && (
        <ul className={`${styles.results} ${results.isFetching ? styles.stale : ''}`}>
          {(results.data ?? []).map((r) => {
            const isSelected = selected?.tmdbId === r.tmdbId
            const inLibrary = r.libraryId !== null
            return (
              <li key={r.tmdbId} className={`${styles.item} ${isSelected ? styles.selectedRow : ''} ${inLibrary ? styles.owned : ''}`}>
                <div className={styles.resultRow}>
                  <button
                    type="button"
                    className={styles.result}
                    onClick={() => setSelected(isSelected ? null : r)}
                    disabled={inLibrary}
                    aria-expanded={inLibrary ? undefined : isSelected}
                  >
                    <span className={styles.thumb}>
                      <Poster path={r.posterPath} title={r.title} size="w92" />
                    </span>
                    <span className={styles.text}>
                      <span className={styles.title}>
                        {r.title} {r.year > 0 && <span className={styles.year}>{r.year}</span>}
                      </span>
                      {r.originalTitle !== r.title && <span className={styles.original}>{r.originalTitle}</span>}
                      <span className={styles.overview}>{r.overview ?? 'No description.'}</span>
                    </span>
                  </button>
                  {inLibrary ? (
                    <Link className={`${ui.pill} ${ui.pillOk} ${styles.inLibrary}`} to={`/library/${path}/${r.libraryId}`} onClick={onClose}>
                      <Icon name="check" size={12} />
                      In library
                    </Link>
                  ) : (
                    <Icon name={isSelected ? 'down' : 'right'} size={16} className={styles.chevron} />
                  )}
                </div>
                {isSelected && <AddOptions type={type} item={r} onDone={onClose} />}
              </li>
            )
          })}
        </ul>
      )}
      <p className={styles.attribution}>Search results from The Movie Database (TMDB).</p>
    </Modal>
  )
}

function AddOptions({ type, item, onDone }: { type: MediaType; item: LookupResult; onDone: () => void }) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const rootFolders = useQuery({ queryKey: queryKeys.rootFolders, queryFn: libraryApi.rootFolders })
  const folders = (rootFolders.data ?? []).filter((r) => r.mediaType === type)
  const [rootFolderId, setRootFolderId] = useState<number | null>(null)
  const [monitored, setMonitored] = useState(true)
  const [qualityProfileId, setQualityProfileId] = useState<number | null>(null)
  const [seriesType, setSeriesType] = useState<SeriesType>(item.suggestedSeriesType ?? 'standard')
  const effectiveRoot = rootFolderId ?? folders[0]?.id ?? null
  // "Start search for missing": the default comes from Settings → Indexers ("Search when adding").
  const options = useQuery({ queryKey: indexerOptionsKeys.options, queryFn: indexerOptionsApi.options, staleTime: 60_000 })
  const [search, setSearch] = useState<boolean | null>(null)
  const searchForMissing = monitored && (search ?? options.data?.searchOnAdd ?? false)

  const add = useMutation({
    mutationFn: () =>
      libraryApi.add(type, item.tmdbId, effectiveRoot!, monitored, qualityProfileId, searchForMissing, type === 'Series' ? seriesType : null),
    onSuccess: async (created) => {
      await queryClient.invalidateQueries({ queryKey: type === 'Movie' ? queryKeys.movies : queryKeys.series })
      onDone()
      void navigate(`/library/${type === 'Movie' ? 'movies' : 'series'}/${created.id}`)
    },
  })

  return (
    <div className={styles.options}>
      <div className={styles.fields}>
        <label className={ui.field}>
          <span>Root folder</span>
          <select className={ui.select} value={effectiveRoot ?? ''} onChange={(e) => setRootFolderId(Number(e.target.value))}>
            {folders.map((f) => (
              <option key={f.id} value={f.id}>
                {f.path}
              </option>
            ))}
          </select>
        </label>
        <label className={ui.field}>
          <span>Quality profile</span>
          <ProfileSelect value={qualityProfileId} onChange={setQualityProfileId} />
        </label>
        {type === 'Series' && <SeriesTypeField value={seriesType} suggested={item.suggestedSeriesType} onChange={setSeriesType} />}
      </div>
      <label className={styles.switchRow}>
        <span className={ui.switch}>
          <input type="checkbox" checked={monitored} onChange={(e) => setMonitored(e.target.checked)} />
          <span />
        </span>
        <span>Monitored{type === 'Series' ? ' (all regular seasons)' : ''}</span>
      </label>
      <label className={styles.switchRow}>
        <span className={ui.switch}>
          <input type="checkbox" checked={searchForMissing} disabled={!monitored} onChange={(e) => setSearch(e.target.checked)} />
          <span />
        </span>
        <span>Start search for missing {type === 'Movie' ? 'movie' : 'episodes'}</span>
      </label>
      <div className={styles.optionActions}>
        <button className={ui.primary} type="button" disabled={!effectiveRoot || add.isPending} onClick={() => add.mutate()}>
          <Icon name="plus" size={15} />
          {add.isPending ? 'Adding…' : `Add ${item.title}`}
        </button>
        {add.error && <span className={ui.error}>{add.error.message}</span>}
      </div>
    </div>
  )
}
