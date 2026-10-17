import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { libraryApi, queryKeys, type LookupResult, type MediaType } from '../api/library'
import Modal from '../components/Modal'
import Poster from '../components/Poster'
import ProfileSelect from '../components/ProfileSelect'
import ui from '../components/ui.module.css'
import styles from './AddDialog.module.css'

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
      <input
        className={ui.input}
        type="search"
        autoFocus
        placeholder={type === 'Movie' ? 'Search TMDB, e.g. Dune' : 'Search TMDB, e.g. Breaking Bad'}
        value={term}
        onChange={(e) => {
          setTerm(e.target.value)
          setSelected(null)
        }}
      />

      {results.isFetching && <p className={ui.muted}>Searching…</p>}
      {results.isError && <p className={ui.error}>{results.error.message}</p>}
      {results.data && results.data.length === 0 && <p className={ui.muted}>No results on TMDB.</p>}

      <ul className={styles.results}>
        {(results.data ?? []).map((r) => (
          <li key={r.tmdbId} className={selected?.tmdbId === r.tmdbId ? styles.selectedRow : undefined}>
            <button type="button" className={styles.result} onClick={() => setSelected(r)} disabled={r.libraryId !== null}>
              <span className={styles.thumb}>
                <Poster path={r.posterPath} title={r.title} size="w92" />
              </span>
              <span className={styles.text}>
                <span className={styles.title}>
                  {r.title} {r.year > 0 && <span className={ui.muted}>({r.year})</span>}
                </span>
                {r.originalTitle !== r.title && <span className={styles.original}>{r.originalTitle}</span>}
                <span className={styles.overview}>{r.overview ?? 'No description.'}</span>
              </span>
            </button>
            {r.libraryId !== null && (
              <Link className={styles.inLibrary} to={`/library/${path}/${r.libraryId}`} onClick={onClose}>
                In library →
              </Link>
            )}
            {selected?.tmdbId === r.tmdbId && <AddOptions type={type} item={r} onDone={onClose} />}
          </li>
        ))}
      </ul>
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
  const effectiveRoot = rootFolderId ?? folders[0]?.id ?? null

  const add = useMutation({
    mutationFn: () => libraryApi.add(type, item.tmdbId, effectiveRoot!, monitored, qualityProfileId),
    onSuccess: async (created) => {
      await queryClient.invalidateQueries({ queryKey: type === 'Movie' ? queryKeys.movies : queryKeys.series })
      onDone()
      void navigate(`/library/${type === 'Movie' ? 'movies' : 'series'}/${created.id}`)
    },
  })

  return (
    <div className={styles.options}>
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
      <label className={ui.check}>
        <input type="checkbox" checked={monitored} onChange={(e) => setMonitored(e.target.checked)} />
        <span>Monitored{type === 'Series' ? ' (all regular seasons)' : ''}</span>
      </label>
      <div className={styles.optionActions}>
        <button className={ui.primary} type="button" disabled={!effectiveRoot || add.isPending} onClick={() => add.mutate()}>
          {add.isPending ? 'Adding…' : `Add ${item.title}`}
        </button>
        {add.error && <span className={ui.error}>{add.error.message}</span>}
      </div>
    </div>
  )
}
