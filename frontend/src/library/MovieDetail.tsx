import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useNavigate, useParams } from 'react-router'
import { libraryApi, queryKeys } from '../api/library'
import Icon from '../components/Icon'
import Loader from '../components/Loader'
import ProfileSelect from '../components/ProfileSelect'
import ui from '../components/ui.module.css'
import { formatBytes, formatDate, formatRuntime } from '../lib/format'
import RenameDialog from '../mediaManagement/RenameDialog'
import page from '../pages/Page.module.css'
import { useIsAdmin } from '../status/useOngoing'
import DetailHeader from './DetailHeader'
import styles from './Detail.module.css'
import HistoryPanel from './HistoryPanel'
import MediaInfoPanel from './MediaInfoPanel'
import MoreMenu, { type MoreItem } from './MoreMenu'
import OrganizeDialog from './OrganizeDialog'
import PathRow from './PathRow'
import RemoveButton from './RemoveButton'
import RemoveDialog from './RemoveDialog'
import SearchButtons from './SearchButtons'
import { useDetailActions } from './useDetailActions'
import AlternativeTitles from './AlternativeTitles'

export default function MovieDetail() {
  const id = Number(useParams().id)
  const movie = useQuery({ queryKey: queryKeys.movie(id), queryFn: () => libraryApi.movie(id) })
  const { monitor, refresh, remove, assignProfile } = useDetailActions('Movie', id)
  const admin = useIsAdmin()
  const [renaming, setRenaming] = useState(false)
  const [organizing, setOrganizing] = useState(false)
  const [removing, setRemoving] = useState(false)
  const navigate = useNavigate()

  if (movie.isError) return <p className={ui.error}>{movie.error.message}</p>
  if (!movie.data) return <Loader size="lg" text="Loading the movie…" />

  const m = movie.data
  const more: MoreItem[] = admin
    ? [
        ...(m.hasFile ? [{ label: 'Rename files', icon: 'rename' as const, onClick: () => setRenaming(true) }] : []),
        { label: 'Organize', icon: 'folder', onClick: () => setOrganizing(true) },
        { label: 'Remove', icon: 'trash', onClick: () => setRemoving(true), danger: true },
      ]
    : []

  return (
    <div className={page.page}>
      <DetailHeader
        title={m.title}
        posterPath={m.posterPath}
        backdropPath={m.backdropPath}
        back={{ to: '/library/movies', label: 'Movies' }}
        facts={[m.year > 0 && String(m.year), formatRuntime(m.runtime), m.genres.slice(0, 3).join(', '), m.status]}
        overview={m.overview}
        badges={
          <>
            <span className={`${ui.badge} ${m.hasFile ? ui.ok : m.monitored ? ui.missing : ui.idle}`}>
              {m.hasFile ? 'On disk' : 'Missing'}
            </span>
            <span className={`${ui.badge} ${m.monitored ? ui.ok : ui.idle}`}>{m.monitored ? 'Monitored' : 'Unmonitored'}</span>
            {m.file && <span className={`${ui.pill} ${ui.num}`}>{formatBytes(m.file.size)}</span>}
          </>
        }
        actions={
          <>
            <SearchButtons
              command={{ name: 'MoviesSearch', movieIds: [m.id] }}
              mediaType="Movie"
              id={m.id}
              target={{ kind: 'movie', movieId: m.id }}
              modalTitle={m.year > 0 ? `${m.title} (${m.year})` : m.title}
              primary
            />
            <span className={styles.divider} aria-hidden="true" />
            <button className={ui.button} type="button" onClick={() => monitor.mutate(!m.monitored)} disabled={monitor.isPending}>
              <Icon name={m.monitored ? 'eyeoff' : 'eye'} size={15} />
              {m.monitored ? 'Unmonitor' : 'Monitor'}
            </button>
            <button className={ui.button} type="button" onClick={() => refresh.mutate()} disabled={refresh.isPending} title="Refresh metadata and rescan the folder">
              <Icon name={refresh.isSuccess ? 'check' : 'restart'} size={15} />
              {refresh.isSuccess ? 'Refresh queued' : 'Refresh & rescan'}
            </button>
            <ProfileSelect
              className={`${ui.select} ${styles.profile}`}
              value={m.qualityProfileId}
              onChange={(profileId) => assignProfile.mutate(profileId)}
              disabled={assignProfile.isPending}
            />
            {admin ? (
              <MoreMenu items={more} />
            ) : (
              <RemoveButton onConfirm={() => remove.mutate()} pending={remove.isPending} item={{ mediaType: 'Movie', tmdbId: m.tmdbId, title: m.title, year: m.year }} />
            )}
            {assignProfile.error && <span className={ui.error}>{assignProfile.error.message}</span>}
          </>
        }
      />

      <section className={ui.panel}>
        <div className={ui.panelHead}>
          <h2 className={styles.panelTitle}>
            <Icon name="folder" size={17} />
            Files
          </h2>
          {m.file && <span className={styles.panelCount}>{formatBytes(m.file.size)}</span>}
        </div>
        <div className={styles.paths}>
          <PathRow label="Folder" path={m.folderPath} />
          <PathRow label="Video file" path={m.file ? m.file.relativePath : null} empty="No video file found" />
        </div>
        <dl className={styles.kv}>
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
              <Icon name="external" size={12} />
            </a>
            {m.imdbId && (
              <a href={`https://www.imdb.com/title/${m.imdbId}`} target="_blank" rel="noreferrer">
                IMDb
                <Icon name="external" size={12} />
              </a>
            )}
          </dd>
        </dl>
      </section>

      {m.file && <MediaInfoPanel movieId={m.id} />}
      <AlternativeTitles type="Movie" id={m.id} />
      <HistoryPanel movieId={m.id} />

      {organizing && <OrganizeDialog type="Movie" ids={[m.id]} onClose={() => setOrganizing(false)} />}
      {removing && (
        <RemoveDialog
          type="Movie"
          items={[{ id: m.id, title: m.year > 0 ? `${m.title} (${m.year})` : m.title, folder: m.folderPath, size: m.file?.size ?? 0 }]}
          onClose={() => setRemoving(false)}
          onRemoved={() => void navigate('/library/movies')}
          listItem={{ mediaType: 'Movie', tmdbId: m.tmdbId, title: m.title, year: m.year }}
        />
      )}
      {renaming && <RenameDialog type="Movie" id={m.id} title={m.year > 0 ? `${m.title} (${m.year})` : m.title} onClose={() => setRenaming(false)} />}
    </div>
  )
}
