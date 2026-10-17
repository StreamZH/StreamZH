import { useQuery } from '@tanstack/react-query'
import { Link, useParams } from 'react-router'
import { libraryApi, queryKeys } from '../api/library'
import ui from '../components/ui.module.css'
import { formatBytes, formatDate, formatRuntime } from '../lib/format'
import page from '../pages/Page.module.css'
import DetailHeader from './DetailHeader'
import styles from './Detail.module.css'
import RemoveButton from './RemoveButton'
import { useDetailActions } from './useDetailActions'

export default function MovieDetail() {
  const id = Number(useParams().id)
  const movie = useQuery({ queryKey: queryKeys.movie(id), queryFn: () => libraryApi.movie(id) })
  const { monitor, refresh, remove } = useDetailActions('Movie', id)

  if (movie.isError) return <p className={ui.error}>{movie.error.message}</p>
  if (!movie.data) return <div className={page.page} aria-busy="true" />

  const m = movie.data
  return (
    <div className={page.page}>
      <Link className={styles.back} to="/library/movies">
        ← Movies
      </Link>
      <DetailHeader
        title={m.title}
        posterPath={m.posterPath}
        backdropPath={m.backdropPath}
        facts={[m.year > 0 && String(m.year), formatRuntime(m.runtime), m.genres.slice(0, 3).join(', '), m.status]}
        overview={m.overview}
        badges={
          <>
            <span className={`${ui.badge} ${m.hasFile ? ui.ok : m.monitored ? ui.missing : ui.idle}`}>
              {m.hasFile ? 'On disk' : 'Missing'}
            </span>
            <span className={`${ui.badge} ${m.monitored ? ui.ok : ui.idle}`}>{m.monitored ? 'Monitored' : 'Unmonitored'}</span>
          </>
        }
        actions={
          <>
            <button className={ui.button} type="button" onClick={() => monitor.mutate(!m.monitored)} disabled={monitor.isPending}>
              {m.monitored ? 'Unmonitor' : 'Monitor'}
            </button>
            <button className={ui.button} type="button" onClick={() => refresh.mutate()} disabled={refresh.isPending}>
              {refresh.isSuccess ? 'Refresh queued' : 'Refresh & rescan'}
            </button>
            <RemoveButton onConfirm={() => remove.mutate()} pending={remove.isPending} />
          </>
        }
      />

      <section className={page.panel}>
        <h2 className={page.panelTitle}>Files</h2>
        <dl className={styles.facts2}>
          <dt>Folder</dt>
          <dd className={ui.mono}>{m.folderPath}</dd>
          <dt>File</dt>
          <dd className={ui.mono}>{m.file ? m.file.relativePath : <span className={ui.muted}>No video file found</span>}</dd>
          {m.file && (
            <>
              <dt>Size</dt>
              <dd>{formatBytes(m.file.size)}</dd>
              <dt>Modified</dt>
              <dd>{formatDate(m.file.modified)}</dd>
            </>
          )}
          <dt>Added</dt>
          <dd>{formatDate(m.added)}</dd>
          <dt>Links</dt>
          <dd className={styles.links}>
            <a href={`https://www.themoviedb.org/movie/${m.tmdbId}`} target="_blank" rel="noreferrer">
              TMDB
            </a>
            {m.imdbId && (
              <a href={`https://www.imdb.com/title/${m.imdbId}`} target="_blank" rel="noreferrer">
                IMDb
              </a>
            )}
          </dd>
        </dl>
      </section>
    </div>
  )
}
