import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useParams } from 'react-router'
import { libraryApi, queryKeys, type Episode, type Season } from '../api/library'
import Icon from '../components/Icon'
import ProfileSelect from '../components/ProfileSelect'
import ui from '../components/ui.module.css'
import { formatBytes, formatDate, isAired } from '../lib/format'
import page from '../pages/Page.module.css'
import DetailHeader from './DetailHeader'
import styles from './Detail.module.css'
import HistoryPanel from './HistoryPanel'
import RemoveButton from './RemoveButton'
import search from './ReleaseSearch.module.css'
import ReleaseSearchModal from './ReleaseSearchModal'
import SearchButtons from './SearchButtons'
import { useDetailActions } from './useDetailActions'

export default function SeriesDetail() {
  const id = Number(useParams().id)
  const detail = useQuery({ queryKey: queryKeys.seriesDetail(id), queryFn: () => libraryApi.seriesDetail(id) })
  const { monitor, refresh, remove, assignProfile } = useDetailActions('Series', id)

  if (detail.isError) return <p className={ui.error}>{detail.error.message}</p>
  if (!detail.data) return <div className={page.page} aria-busy="true" />

  const { series: s, seasons, episodes } = detail.data
  const loading = s.lastRefreshed === null

  return (
    <div className={page.page}>
      <Link className={styles.back} to="/library/series">
        ← Series
      </Link>
      <DetailHeader
        title={s.title}
        posterPath={s.posterPath}
        backdropPath={s.backdropPath}
        facts={[s.year > 0 && String(s.year), s.network, s.genres.slice(0, 3).join(', '), s.status]}
        overview={s.overview}
        badges={
          <>
            <span className={`${ui.badge} ${loading ? ui.waiting : s.fileCount >= s.airedCount && s.airedCount > 0 ? ui.ok : ui.missing}`}>
              {loading ? 'Loading episodes…' : `${s.fileCount} / ${s.airedCount} aired episodes`}
            </span>
            <span className={`${ui.badge} ${s.monitored ? ui.ok : ui.idle}`}>{s.monitored ? 'Monitored' : 'Unmonitored'}</span>
            {s.sizeOnDisk > 0 && <span className={ui.badge}>{formatBytes(s.sizeOnDisk)}</span>}
          </>
        }
        actions={
          <>
            <button className={ui.button} type="button" onClick={() => monitor.mutate(!s.monitored)} disabled={monitor.isPending}>
              {s.monitored ? 'Unmonitor' : 'Monitor'}
            </button>
            <button className={ui.button} type="button" onClick={() => refresh.mutate()} disabled={refresh.isPending}>
              {refresh.isSuccess ? 'Refresh queued' : 'Refresh & rescan'}
            </button>
            <label className={styles.profile}>
              <span>Profile</span>
              <ProfileSelect
                className=""
                value={s.qualityProfileId}
                onChange={(profileId) => assignProfile.mutate(profileId)}
                disabled={assignProfile.isPending}
              />
            </label>
            <RemoveButton onConfirm={() => remove.mutate()} pending={remove.isPending} />
            {assignProfile.error && <span className={ui.error}>{assignProfile.error.message}</span>}
          </>
        }
      />

      <p className={`${ui.mono} ${ui.muted}`}>{s.folderPath}</p>

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

  return (
    <section className={page.panel}>
      <div className={styles.seasonHead}>
        <button type="button" className={styles.seasonToggle} onClick={() => setOpen((o) => !o)} aria-expanded={open}>
          <span className={styles.chevron}>{open ? '▾' : '▸'}</span>
          <span className={styles.seasonTitle}>{seasonName}</span>
          <span className={`${ui.badge} ${season.fileCount >= aired && aired > 0 ? ui.ok : season.fileCount > 0 ? ui.waiting : ui.idle}`}>
            {season.fileCount} / {aired} aired
          </span>
        </button>
        <div className={search.seasonTools}>
          <SearchButtons
            command={{ name: 'SeasonSearch', seriesId, seasonNumber: season.seasonNumber }}
            mediaType="Series"
            id={seriesId}
            target={{ kind: 'season', seriesId, seasonNumber: season.seasonNumber }}
            modalTitle={`${seriesTitle} · ${seasonName}`}
            searchLabel="Search season"
            small
          />
          <label className={ui.check}>
            <input type="checkbox" checked={season.monitored} onChange={(e) => monitor.mutate(e.target.checked)} disabled={monitor.isPending} />
            <span>Monitored</span>
          </label>
        </div>
      </div>

      {open && (
        <table className={styles.episodes}>
          <tbody>
            {episodes.map((e) => (
              <tr key={e.id} className={e.monitored ? undefined : styles.unmonitored}>
                <td className={styles.epNumber}>{e.episodeNumber}</td>
                <td>
                  <span className={styles.epTitle}>{e.title}</span>
                  {e.file && <span className={styles.epFile}>{e.file.relativePath}</span>}
                </td>
                <td className={styles.epDate}>{formatDate(e.airDate)}</td>
                <td className={styles.epStatus}>
                  <EpisodeStatus episode={e} />
                </td>
                <td className={search.epSearch}>
                  <button
                    type="button"
                    className={search.iconButton}
                    onClick={() => setManualEpisode(e)}
                    title={`Manual search for ${code(e)}`}
                    aria-label={`Manual search for ${code(e)}`}
                  >
                    <Icon name="indexers" size={16} />
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
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
  if (episode.file) return <span className={`${ui.badge} ${ui.ok}`}>{formatBytes(episode.file.size)}</span>
  if (!isAired(episode.airDate)) return <span className={`${ui.badge} ${ui.idle}`}>Not aired</span>
  if (!episode.monitored) return <span className={`${ui.badge} ${ui.idle}`}>Unmonitored</span>
  return <span className={`${ui.badge} ${ui.missing}`}>Missing</span>
}
