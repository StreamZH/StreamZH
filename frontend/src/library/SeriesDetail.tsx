import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { useNavigate, useParams } from 'react-router'
import { libraryApi, queryKeys, type Episode, type Season } from '../api/library'
import Icon from '../components/Icon'
import Loader from '../components/Loader'
import ProfileSelect from '../components/ProfileSelect'
import ui from '../components/ui.module.css'
import { formatBytes, formatDate, isAired } from '../lib/format'
import RenameDialog from '../mediaManagement/RenameDialog'
import page from '../pages/Page.module.css'
import { useIsAdmin } from '../status/useOngoing'
import DetailHeader from './DetailHeader'
import styles from './Detail.module.css'
import HistoryPanel from './HistoryPanel'
import MoreMenu, { type MoreItem } from './MoreMenu'
import OrganizeDialog from './OrganizeDialog'
import PathRow from './PathRow'
import RemoveButton from './RemoveButton'
import RemoveDialog from './RemoveDialog'
import ReleaseSearchModal from './ReleaseSearchModal'
import SearchButtons from './SearchButtons'
import { useDetailActions } from './useDetailActions'

export default function SeriesDetail() {
  const id = Number(useParams().id)
  const detail = useQuery({ queryKey: queryKeys.seriesDetail(id), queryFn: () => libraryApi.seriesDetail(id) })
  const { monitor, refresh, remove, assignProfile } = useDetailActions('Series', id)
  const admin = useIsAdmin()
  const [renaming, setRenaming] = useState(false)
  const [organizing, setOrganizing] = useState(false)
  const [removing, setRemoving] = useState(false)
  const navigate = useNavigate()

  if (detail.isError) return <p className={ui.error}>{detail.error.message}</p>
  if (!detail.data) return <Loader size="lg" text="Loading the series…" />

  const { series: s, seasons, episodes } = detail.data
  const loading = s.lastRefreshed === null
  const more: MoreItem[] = admin
    ? [
        ...(s.fileCount > 0 ? [{ label: 'Rename files', icon: 'rename' as const, onClick: () => setRenaming(true) }] : []),
        { label: 'Organize', icon: 'folder', onClick: () => setOrganizing(true) },
        { label: 'Remove', icon: 'trash', onClick: () => setRemoving(true), danger: true },
      ]
    : []

  return (
    <div className={page.page}>
      <DetailHeader
        title={s.title}
        posterPath={s.posterPath}
        backdropPath={s.backdropPath}
        back={{ to: '/library/series', label: 'Series' }}
        facts={[s.year > 0 && String(s.year), s.network, s.genres.slice(0, 3).join(', '), s.status]}
        overview={s.overview}
        badges={
          <>
            <span className={`${ui.badge} ${ui.num} ${loading ? ui.waiting : s.fileCount >= s.airedCount && s.airedCount > 0 ? ui.ok : ui.missing}`}>
              {loading ? 'Loading episodes…' : `${s.fileCount} / ${s.airedCount} aired episodes`}
            </span>
            <span className={`${ui.badge} ${s.monitored ? ui.ok : ui.idle}`}>{s.monitored ? 'Monitored' : 'Unmonitored'}</span>
            {s.sizeOnDisk > 0 && <span className={`${ui.pill} ${ui.num}`}>{formatBytes(s.sizeOnDisk)}</span>}
          </>
        }
        actions={
          <>
            <button className={ui.button} type="button" onClick={() => monitor.mutate(!s.monitored)} disabled={monitor.isPending}>
              <Icon name={s.monitored ? 'eyeoff' : 'eye'} size={15} />
              {s.monitored ? 'Unmonitor' : 'Monitor'}
            </button>
            <button className={ui.button} type="button" onClick={() => refresh.mutate()} disabled={refresh.isPending} title="Refresh metadata and rescan the folder">
              <Icon name={refresh.isSuccess ? 'check' : 'restart'} size={15} />
              {refresh.isSuccess ? 'Refresh queued' : 'Refresh & rescan'}
            </button>
            <ProfileSelect
              className={`${ui.select} ${styles.profile}`}
              value={s.qualityProfileId}
              onChange={(profileId) => assignProfile.mutate(profileId)}
              disabled={assignProfile.isPending}
            />
            {admin ? (
              <MoreMenu items={more} />
            ) : (
              <RemoveButton onConfirm={() => remove.mutate()} pending={remove.isPending} item={{ mediaType: 'Series', tmdbId: s.tmdbId, title: s.title, year: s.year }} />
            )}
            {assignProfile.error && <span className={ui.error}>{assignProfile.error.message}</span>}
          </>
        }
      />

      <PathRow label="Folder" path={s.folderPath} />
      {renaming && <RenameDialog type="Series" id={s.id} title={s.title} onClose={() => setRenaming(false)} />}

      {seasons.map((season) => (
        <SeasonPanel
          key={season.seasonNumber}
          seriesId={s.id}
          seriesTitle={s.title}
          season={season}
          episodes={episodes.filter((e) => e.seasonNumber === season.seasonNumber)}
          defaultOpen={season.seasonNumber === Math.max(...seasons.map((x) => x.seasonNumber))}
        />
      ))}

      {organizing && <OrganizeDialog type="Series" ids={[s.id]} onClose={() => setOrganizing(false)} />}
      {removing && (
        <RemoveDialog
          type="Series"
          items={[{ id: s.id, title: s.title, folder: s.folderPath, size: s.sizeOnDisk }]}
          onClose={() => setRemoving(false)}
          onRemoved={() => void navigate('/library/series')}
          listItem={{ mediaType: 'Series', tmdbId: s.tmdbId, title: s.title, year: s.year }}
        />
      )}
      <HistoryPanel seriesId={s.id} />
    </div>
  )
}

function SeasonPanel({
  seriesId,
  seriesTitle,
  season,
  episodes,
  defaultOpen,
}: {
  seriesId: number
  seriesTitle: string
  season: Season
  episodes: Episode[]
  defaultOpen: boolean
}) {
  const [open, setOpen] = useState(defaultOpen)
  const [manualEpisode, setManualEpisode] = useState<Episode | null>(null)
  const queryClient = useQueryClient()
  const aired = episodes.filter((e) => isAired(e.airDate)).length
  const seasonName = season.seasonNumber === 0 ? 'Specials' : `Season ${season.seasonNumber}`
  const code = (e: Episode) => `S${String(e.seasonNumber).padStart(2, '0')}E${String(e.episodeNumber).padStart(2, '0')}`

  const monitor = useMutation({
    mutationFn: (monitored: boolean) => libraryApi.setSeasonMonitored(seriesId, season.seasonNumber, monitored),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.seriesDetail(seriesId) }),
  })

  const complete = season.fileCount >= aired && aired > 0
  return (
    <section className={`${ui.panel} ${styles.season} ${open ? styles.open : ''}`}>
      <div className={styles.seasonHead}>
        <button type="button" className={styles.seasonToggle} onClick={() => setOpen((o) => !o)} aria-expanded={open}>
          <Icon name="right" size={15} className={styles.chevron} />
          <span className={styles.seasonTitle}>{seasonName}</span>
          <span className={`${ui.pill} ${ui.num} ${complete ? ui.pillOk : season.fileCount > 0 ? ui.pillWarn : ''} ${styles.eventPill}`}>
            {season.fileCount} / {aired} aired
          </span>
        </button>
        <div className={styles.seasonTools}>
          <SearchButtons
            command={{ name: 'SeasonSearch', seriesId, seasonNumber: season.seasonNumber }}
            mediaType="Series"
            id={seriesId}
            target={{ kind: 'season', seriesId, seasonNumber: season.seasonNumber }}
            modalTitle={`${seriesTitle} · ${seasonName}`}
            searchLabel="Search season"
            small
          />
          <label className={styles.monitorSwitch}>
            <span className={ui.switch}>
              <input type="checkbox" checked={season.monitored} onChange={(e) => monitor.mutate(e.target.checked)} disabled={monitor.isPending} />
              <span />
            </span>
            <span className={styles.switchText}>Monitored</span>
          </label>
        </div>
      </div>

      {open && (
        <div className={styles.seasonBody}>
          <table className={ui.table}>
            <thead>
              <tr>
                <th className={styles.epNumber}>#</th>
                <th>Title</th>
                <th className={styles.epDate}>Aired</th>
                <th className={styles.epStatus}>Status</th>
                <th className={styles.epSearch} aria-label="Manual search" />
              </tr>
            </thead>
            <tbody>
              {episodes.map((e) => (
                <tr key={e.id} className={e.monitored ? undefined : styles.unmonitored}>
                  <td className={styles.epNumber}>{String(e.episodeNumber).padStart(2, '0')}</td>
                  <td>
                    <span className={styles.epTitle}>{e.title}</span>
                    {e.file && <span className={styles.epFile}>{e.file.relativePath}</span>}
                  </td>
                  <td className={styles.epDate}>{formatDate(e.airDate)}</td>
                  <td className={styles.epStatus}>
                    <EpisodeStatus episode={e} />
                  </td>
                  <td className={styles.epSearch}>
                    <button
                      type="button"
                      className={`${ui.ghost} ${ui.sm} ${ui.icon}`}
                      onClick={() => setManualEpisode(e)}
                      title={`Manual search for ${code(e)}`}
                      aria-label={`Manual search for ${code(e)}`}
                    >
                      <Icon name="search" size={15} />
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {manualEpisode && (
        <ReleaseSearchModal
          title={`${seriesTitle} · ${code(manualEpisode)}`}
          target={{ kind: 'episode', episodeId: manualEpisode.id }}
          onClose={() => setManualEpisode(null)}
        />
      )}
    </section>
  )
}

function EpisodeStatus({ episode }: { episode: Episode }) {
  if (episode.file) return <span className={`${ui.badge} ${ui.ok} ${ui.num}`}>{formatBytes(episode.file.size)}</span>
  if (!isAired(episode.airDate)) return <span className={`${ui.badge} ${ui.idle}`}>Not aired</span>
  if (!episode.monitored) return <span className={`${ui.badge} ${ui.idle}`}>Unmonitored</span>
  return <span className={`${ui.badge} ${ui.missing}`}>Missing</span>
}
