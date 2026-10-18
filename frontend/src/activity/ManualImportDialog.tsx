import { useMutation, useQuery } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { alternativeTitlesApi } from '../api/alternativeTitles'
import { ApiError } from '../api/client'
import type { DownloadItem } from '../api/downloads'
import { libraryApi, queryKeys, type MediaType } from '../api/library'
import { mediaManagementApi, mediaManagementKeys, type ImportFileCandidate } from '../api/mediaManagement'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import { formatBytes } from '../lib/format'
import styles from './Activity.module.css'
import { parseEpisodes } from './importHooks'

function label(candidate: ImportFileCandidate): string {
  if (candidate.seasonNumber == null || candidate.episodeNumbers.length === 0) return ''
  const pad = (n: number) => String(n).padStart(2, '0')
  return `S${pad(candidate.seasonNumber)}` + candidate.episodeNumbers.map((e) => `E${pad(e)}`).join('')
}

/**
 * Manual import (Radarr/Sonarr "Manual Import"): for a finished download that belongs to nothing, or whose import failed.
 * Pick the movie or series; for a series, check the episodes of every file.
 */
export default function ManualImportDialog({
  item,
  onClose,
  onQueued,
}: {
  item: DownloadItem
  onClose: () => void
  onQueued: (title: string) => void
}) {
  const [type, setType] = useState<MediaType>(item.mediaType ?? (item.category === 'series' ? 'Series' : 'Movie'))
  const [movieId, setMovieId] = useState<number | null>(item.movieId)
  const [seriesId, setSeriesId] = useState<number | null>(item.seriesId)
  const [filter, setFilter] = useState('')
  const [chosenFile, setChosenFile] = useState<string | null>(null)
  const [episodeText, setEpisodeText] = useState<Record<string, string>>({})
  const [skipped, setSkipped] = useState<Set<string>>(new Set())

  // A download that belongs to nothing: preselect the library item its name stands for (any title, also German ones).
  const unlinked = item.movieId == null && item.seriesId == null
  const suggestion = useQuery({ queryKey: ['library-match', item.title], queryFn: () => alternativeTitlesApi.match(item.title), enabled: unlinked })
  const [suggested, setSuggested] = useState(!unlinked)
  if (!suggested && suggestion.data) {
    setSuggested(true)
    const match = suggestion.data.match
    if (match && movieId == null && seriesId == null) {
      setType(match.mediaType === 'movie' ? 'Movie' : 'Series')
      if (match.mediaType === 'movie') setMovieId(match.id)
      else setSeriesId(match.id)
    }
  }

  const movies = useQuery({ queryKey: queryKeys.movies, queryFn: libraryApi.movies, enabled: type === 'Movie', staleTime: 60_000 })
  const series = useQuery({ queryKey: queryKeys.series, queryFn: libraryApi.series, enabled: type === 'Series', staleTime: 60_000 })
  const detail = useQuery({
    queryKey: queryKeys.seriesDetail(seriesId ?? 0),
    queryFn: () => libraryApi.seriesDetail(seriesId!),
    enabled: type === 'Series' && seriesId != null,
  })
  const files = useQuery({
    queryKey: mediaManagementKeys.files(item.id, type === 'Series' ? seriesId : null),
    queryFn: () => mediaManagementApi.files(item.id, type === 'Series' ? seriesId : null),
  })

  const options = useMemo(() => {
    const term = filter.trim().toLowerCase()
    const list =
      type === 'Movie'
        ? (movies.data ?? []).map((m) => ({ id: m.id, label: m.year > 0 ? `${m.title} (${m.year})` : m.title }))
        : (series.data ?? []).map((s) => ({ id: s.id, label: s.year > 0 ? `${s.title} (${s.year})` : s.title }))
    return term ? list.filter((o) => o.label.toLowerCase().includes(term)) : list
  }, [filter, movies.data, series.data, type])

  // With only one candidate left (filter or a library of one), it is the choice.
  const chosen = type === 'Movie' ? movieId : seriesId
  const selected = chosen ?? (options.length === 1 ? options[0].id : null)
  if (chosen == null && selected != null) {
    if (type === 'Movie') setMovieId(selected)
    else setSeriesId(selected)
  }
  const candidates = files.data ?? []
  const largest = candidates.reduce<ImportFileCandidate | null>((a, b) => (a == null || b.size > a.size ? b : a), null)
  const movieFile = chosenFile ?? largest?.relativePath ?? null
  const episodes = detail.data?.episodes ?? []

  const fileEpisodes = (c: ImportFileCandidate): number[] | null => {
    const text = episodeText[c.relativePath]
    if (text === undefined) return c.episodeIds.length > 0 ? c.episodeIds : null
    return parseEpisodes(text, episodes)
  }
  const seriesFiles = candidates.filter((c) => !skipped.has(c.relativePath))
  const invalid = type === 'Series' && seriesFiles.some((c) => fileEpisodes(c) == null)

  const submit = useMutation({
    mutationFn: () =>
      mediaManagementApi.manualImport(item.id, {
        mediaType: type,
        movieId: type === 'Movie' ? (movieId ?? undefined) : undefined,
        seriesId: type === 'Series' ? (seriesId ?? undefined) : undefined,
        files:
          type === 'Movie'
            ? movieFile
              ? [{ path: movieFile, episodeIds: [] }]
              : []
            : seriesFiles.map((c) => ({ path: c.relativePath, episodeIds: fileEpisodes(c) ?? [] })),
      }),
    onSuccess: () => onQueued(item.title),
  })
  const fieldErrors = submit.error instanceof ApiError ? Object.values(submit.error.fieldErrors).flat() : []

  return (
    <Modal title="Import download" onClose={onClose}>
      <div className={styles.choice}>
        <p className={styles.dialogText}>
          <span className={ui.mono}>{item.title}</span>
          <br />
          Choose where it belongs. The files are moved into the library and renamed like an automatic import; an existing file is replaced
          even when it is better.
        </p>

        <div className={`${ui.seg} ${styles.segStart}`} role="group" aria-label="Type">
          {(['Movie', 'Series'] as const).map((t) => (
            <button key={t} type="button" aria-pressed={type === t} onClick={() => setType(t)}>
              {t === 'Movie' ? 'Movie' : 'Series'}
            </button>
          ))}
        </div>

        <label className={ui.field}>
          {type === 'Movie' ? 'Movie' : 'Series'}
          <input className={ui.input} placeholder="Filter the library…" value={filter} onChange={(e) => setFilter(e.target.value)} />
          <div className={styles.pickList} role="listbox" aria-label={type === 'Movie' ? 'Movie' : 'Series'}>
            {options.map((o) => (
              <button
                key={o.id}
                type="button"
                role="option"
                aria-selected={selected === o.id}
                className={styles.pickItem}
                onClick={() => (type === 'Movie' ? setMovieId(o.id) : setSeriesId(o.id))}
              >
                {o.label}
              </button>
            ))}
          </div>
          {options.length === 0 && <small>Nothing found. Add the {type === 'Movie' ? 'movie' : 'series'} to the library first.</small>}
        </label>

        {files.isError && <p className={ui.error}>{files.error.message}</p>}
        {candidates.length === 0 && files.isSuccess && <p className={ui.error}>No video file in this download.</p>}

        {type === 'Movie' && candidates.length > 0 && (
          <div className={styles.fileTable} role="radiogroup" aria-label="Video file">
            {candidates.map((c) => (
              <label key={c.relativePath} className={styles.fileRow}>
                <input type="radio" name="movie-file" checked={movieFile === c.relativePath} onChange={() => setChosenFile(c.relativePath)} />
                <span className={styles.fileName}>
                  {c.relativePath}
                  <small>{[formatBytes(c.size), c.quality, c.languages.join(', ')].filter(Boolean).join(' · ')}</small>
                </span>
              </label>
            ))}
          </div>
        )}

        {type === 'Series' && seriesId != null && candidates.length > 0 && (
          <div className={styles.fileTable}>
            {candidates.map((c) => {
              const ids = fileEpisodes(c)
              const off = skipped.has(c.relativePath)
              return (
                <div key={c.relativePath} className={styles.fileRow}>
                  <input
                    type="checkbox"
                    checked={!off}
                    aria-label={`Import ${c.relativePath}`}
                    onChange={(e) =>
                      setSkipped((s) => {
                        const next = new Set(s)
                        if (e.target.checked) next.delete(c.relativePath)
                        else next.add(c.relativePath)
                        return next
                      })
                    }
                  />
                  <span className={styles.fileName}>
                    {c.relativePath}
                    <small className={!off && ids == null ? styles.fileProblem : undefined}>
                      {!off && ids == null
                        ? (c.problem ?? 'Enter the episodes, e.g. S01E02 or S01E02E03')
                        : [formatBytes(c.size), c.quality].filter(Boolean).join(' · ')}
                    </small>
                  </span>
                  <input
                    className={`${ui.input} ${styles.episodeInput}`}
                    value={episodeText[c.relativePath] ?? label(c)}
                    placeholder="S01E01"
                    disabled={off}
                    aria-label={`Episodes in ${c.relativePath}`}
                    onChange={(e) => setEpisodeText((t) => ({ ...t, [c.relativePath]: e.target.value }))}
                  />
                </div>
              )
            })}
          </div>
        )}

        {submit.error && (
          <p className={ui.error} role="alert">
            {fieldErrors.length > 0 ? fieldErrors.join(' ') : submit.error.message}
          </p>
        )}
      </div>
      <div className={styles.dialogActions}>
        <button type="button" className={ui.button} onClick={onClose}>
          Cancel
        </button>
        <button
          type="button"
          className={ui.primary}
          disabled={
            submit.isPending ||
            selected == null ||
            candidates.length === 0 ||
            (type === 'Series' && (seriesFiles.length === 0 || invalid || !detail.data))
          }
          onClick={() => submit.mutate()}
        >
          {submit.isPending ? 'Importing…' : type === 'Series' ? `Import ${seriesFiles.length} file${seriesFiles.length === 1 ? '' : 's'}` : 'Import'}
        </button>
      </div>
    </Modal>
  )
}
