import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useId, useRef, useState } from 'react'
import { Link } from 'react-router'
import { mediaInfoApi, mediaInfoKeys, videoLine, type MediaInfoResource, type MediaInfoTarget, type MediaInfoView } from '../api/mediaInfo'
import Icon from '../components/Icon'
import { Skeleton } from '../components/Loader'
import ui from '../components/ui.module.css'
import { formatAgo, formatDateTime, formatDuration } from '../lib/format'
import styles from './MediaInfoPanel.module.css'

/** Re-analysing writes the answer straight into the cache. */
function useMediaInfo(target: MediaInfoTarget, enabled = true) {
  const queryClient = useQueryClient()
  const key = mediaInfoKeys.target(target)
  const query = useQuery({ queryKey: key, queryFn: () => mediaInfoApi.get(target), enabled })
  const analyze = useMutation({
    mutationFn: () => mediaInfoApi.analyze(target),
    onSuccess: (data) => queryClient.setQueryData(key, data),
  })
  return { query, analyze }
}

/**
 * Movie page: what ffprobe found in the file, like Radarr's media info: the video line («HEVC · 10-bit · 2160p · Dolby
 * Vision»), every audio track with language, codec and channels, the subtitle languages, and «Re-analyse».
 */
export default function MediaInfoPanel({ movieId }: { movieId: number }) {
  const { query, analyze } = useMediaInfo({ kind: 'movie', movieId })
  const data = query.data
  if (data && !data.hasFile) return null

  return (
    <section className={ui.panel}>
      <div className={ui.panelHead}>
        <h2 className={styles.title}>
          <Icon name="film" size={17} />
          Media info
        </h2>
        {data && (
          <div className={styles.headTools}>
            {data.updated && (
              <span className={styles.updated} title={`Analysed ${formatDateTime(data.updated)}`}>
                {formatAgo(data.updated)}
              </span>
            )}
            <AnalyzeButton data={data} pending={analyze.isPending} onClick={() => analyze.mutate()} />
          </div>
        )}
      </div>
      {query.isPending && <Skeleton rows={3} />}
      {query.isError && <p className={ui.error}>{query.error.message}</p>}
      {data && <MediaInfoBody data={data} problem={analyze.data?.problem ?? analyze.error?.message ?? null} />}
    </section>
  )
}

function AnalyzeButton({ data, pending, onClick, small = true }: { data: MediaInfoResource; pending: boolean; onClick: () => void; small?: boolean }) {
  if (!data.ffprobeFound) return null
  return (
    <button type="button" className={`${ui.button} ${small ? ui.sm : ''}`} onClick={onClick} disabled={pending} title="Read the file with ffprobe again">
      <Icon name="restart" size={14} className={pending ? styles.spin : undefined} />
      {pending ? 'Analysing…' : data.mediaInfo ? 'Re-analyse' : 'Analyse now'}
    </button>
  )
}

function MediaInfoBody({ data, problem, compact = false }: { data: MediaInfoResource; problem: string | null; compact?: boolean }) {
  return (
    <>
      {problem && <p className={ui.errBox}>{problem}</p>}
      {!data.mediaInfo && !data.ffprobeFound && (
        <p className={ui.infoBox}>
          ffprobe was not found, so files are not analysed. Install it in <Link to="/settings/downloads#tools">Settings → Downloads → Tools</Link>.
        </p>
      )}
      {!data.mediaInfo && data.ffprobeFound && !problem && (
        <p className={ui.muted}>{data.analyzeEnabled ? 'Not analysed yet. StreamZH reads new files after the next scan.' : 'Analysing video files is switched off (Settings → Media Management).'}</p>
      )}
      {data.mediaInfo && <Details info={data.mediaInfo} compact={compact} />}
    </>
  )
}

function Details({ info, compact }: { info: MediaInfoView; compact: boolean }) {
  const video = info.video
  const facts = [
    video && video.resolution > 0 ? `${video.width}×${video.height}` : null,
    video?.frameRate ? `${trim(video.frameRate)} fps` : null,
    video?.scanType === 'Interlaced' ? 'Interlaced' : null,
    video?.bitrate ? bitrate(video.bitrate) : info.overallBitrate ? bitrate(info.overallBitrate) : null,
    info.container,
    info.runtimeSeconds ? formatDuration(info.runtimeSeconds) : null,
  ].filter(Boolean)

  return (
    <div className={`${styles.body} ${compact ? styles.compact : ''}`}>
      {video && (
        <div className={styles.video}>
          <span className={styles.videoLine}>{videoLine(video)}</span>
          {facts.length > 0 && <span className={styles.facts}>{facts.join(' · ')}</span>}
        </div>
      )}

      {info.audioTracks.length > 0 && (
        <div className={styles.group}>
          <span className={ui.eyebrow}>Audio</span>
          <ul className={styles.tracks}>
            {info.audioTracks.map((a, i) => (
              <li key={i} className={styles.track}>
                <span className={styles.language}>{a.language ?? 'Unknown language'}</span>
                <span className={styles.codec}>
                  {a.codec}
                  {a.channelsText && <span className={ui.num}> {a.channelsText}</span>}
                </span>
                {a.default && <span className={`${ui.tag} ${styles.flag}`}>Default</span>}
                {a.commentary && <span className={`${ui.tag} ${styles.flag}`}>Commentary</span>}
                {!compact && a.title && <span className={styles.trackTitle}>{a.title}</span>}
              </li>
            ))}
          </ul>
        </div>
      )}

      <div className={styles.group}>
        <span className={ui.eyebrow}>Subtitles</span>
        {info.subtitles.length === 0 ? (
          <span className={ui.muted}>None in the file</span>
        ) : (
          <div className={styles.chips}>
            {info.subtitles.map((s, i) => (
              <span key={i} className={styles.chip} title={s.title ?? undefined}>
                {s.language ?? 'Unknown'}
                <span className={styles.chipMeta}>
                  {[s.format, s.forced ? 'Forced' : null, s.hearingImpaired ? 'SDH' : null].filter(Boolean).join(' · ')}
                </span>
              </span>
            ))}
          </div>
        )}
      </div>
    </div>
  )
}

/**
 * Episode row: a small info button with the media info in a popover (loaded when it opens).
 */
export function EpisodeMediaInfo({ seriesId, episodeId, label }: { seriesId: number; episodeId: number; label: string }) {
  const [open, setOpen] = useState(false)
  const id = useId()
  const box = useRef<HTMLSpanElement>(null)
  const { query, analyze } = useMediaInfo({ kind: 'episode', seriesId, episodeId }, open)

  useEffect(() => {
    if (!open) return
    const onDown = (e: MouseEvent) => {
      if (box.current && !box.current.contains(e.target as Node)) setOpen(false)
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false)
    }
    document.addEventListener('mousedown', onDown)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onDown)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])

  return (
    <span className={styles.popoverAnchor} ref={box}>
      <button
        type="button"
        className={`${ui.ghost} ${ui.sm} ${ui.icon} ${styles.infoButton}`}
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        aria-controls={open ? id : undefined}
        aria-label={`Media info of ${label}`}
        title="Media info"
      >
        <Icon name="info" size={15} />
      </button>
      {open && (
        <div id={id} className={styles.popover} role="dialog" aria-label={`Media info of ${label}`}>
          <div className={styles.popoverHead}>
            <strong>Media info</strong>
            {query.data && <AnalyzeButton data={query.data} pending={analyze.isPending} onClick={() => analyze.mutate()} />}
          </div>
          {query.isPending && <Skeleton rows={2} />}
          {query.isError && <p className={ui.error}>{query.error.message}</p>}
          {query.data && <MediaInfoBody data={query.data} problem={analyze.data?.problem ?? analyze.error?.message ?? null} compact />}
        </div>
      )}
    </span>
  )
}

const trim = (n: number) => (Number.isInteger(n) ? String(n) : n.toFixed(3).replace(/0+$/, ''))

function bitrate(bps: number): string {
  return bps >= 1_000_000 ? `${(bps / 1_000_000).toFixed(1)} Mbit/s` : `${Math.round(bps / 1000)} kbit/s`
}
