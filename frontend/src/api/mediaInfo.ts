import { request } from './client'

/** What ffprobe found in a library file (codecs normalised like Radarr). */
export interface MediaInfoView {
  container: string | null
  runtimeSeconds: number | null
  overallBitrate: number | null
  video: MediaVideo | null
  audioTracks: MediaAudioTrack[]
  subtitles: MediaSubtitle[]
}

export type HdrType =
  | 'None'
  | 'Pq'
  | 'Hdr10'
  | 'Hdr10Plus'
  | 'Hlg'
  | 'DolbyVision'
  | 'DolbyVisionHdr10'
  | 'DolbyVisionHdr10Plus'
  | 'DolbyVisionHlg'
  | 'DolbyVisionSdr'

export interface MediaVideo {
  /** ffprobe's codec name: h264, hevc, av1 … */
  codecName: string
  /** As in file names: x264, h265, AV1 … */
  codec: string
  profile: string | null
  bitDepth: number
  width: number
  height: number
  /** 2160, 1080, 720, 576 or 480 (0 = unknown). */
  resolution: number
  frameRate: number | null
  scanType: string | null
  hdr: HdrType
  /** As in file names: DV HDR10, HDR10Plus …; '' for SDR. */
  dynamicRangeType: string
  is3D: boolean
  bitrate: number | null
}

export interface MediaAudioTrack {
  /** AAC, AC3, EAC3 Atmos, TrueHD Atmos, DTS-HD MA … */
  codec: string
  codecName: string
  profile: string | null
  channels: number
  /** "5.1", "2.0". */
  channelsText: string
  /** Language name, or the raw tag when unknown; null when the track has none. */
  language: string | null
  languageCode: string | null
  title: string | null
  default: boolean
  commentary: boolean
  bitrate: number | null
}

export interface MediaSubtitle {
  /** SRT, ASS, PGS, VobSub … */
  format: string
  language: string | null
  languageCode: string | null
  title: string | null
  default: boolean
  forced: boolean
  hearingImpaired: boolean
}

export interface MediaInfoResource {
  path: string | null
  hasFile: boolean
  /** When the file was analysed; null when it was not. */
  updated: string | null
  /** Why the last analysis did not work (only in the answer to Re-analyse). */
  problem: string | null
  ffprobeFound: boolean
  analyzeEnabled: boolean
  mediaInfo: MediaInfoView | null
}

export interface MediaInfoSettings {
  /** Analyse video files with ffprobe (import, library scans, weekly refresh). */
  analyze: boolean
  /** Days between runs of "Refresh media info"; 0 = off. */
  refreshIntervalDays: number
  ffprobeFound: boolean
}

/** A movie, or an episode of a series. */
export type MediaInfoTarget = { kind: 'movie'; movieId: number } | { kind: 'episode'; seriesId: number; episodeId: number }

const path = (target: MediaInfoTarget) =>
  target.kind === 'movie' ? `/mediainfo/movie/${target.movieId}` : `/mediainfo/series/${target.seriesId}/episode/${target.episodeId}`

export const mediaInfoKeys = {
  target: (target: MediaInfoTarget) => ['mediainfo', target.kind, target.kind === 'movie' ? target.movieId : target.episodeId] as const,
  settings: ['settings-mediainfo'] as const,
}

export const mediaInfoApi = {
  get: (target: MediaInfoTarget) => request<MediaInfoResource>('GET', path(target)),
  /** Runs ffprobe on the file now and answers with the result. */
  analyze: (target: MediaInfoTarget) => request<MediaInfoResource>('POST', `${path(target)}/analyze`),
  /** Queues a background job that analyses every episode file of the series again. */
  analyzeSeries: (seriesId: number) => request<void>('POST', `/mediainfo/series/${seriesId}/analyze`),
  settings: () => request<MediaInfoSettings>('GET', '/settings/mediainfo'),
  saveSettings: (settings: MediaInfoSettings) => request<MediaInfoSettings>('PUT', '/settings/mediainfo', settings),
}

/** "HEVC", "AVC", "AV1" … for people (the file name codec is x265/h265 etc.). */
export function videoFormat(video: MediaVideo): string {
  switch (video.codecName) {
    case 'h264':
      return 'AVC'
    case 'hevc':
      return 'HEVC'
    case 'av1':
      return 'AV1'
    case 'vc1':
      return 'VC-1'
    case 'mpeg2video':
      return 'MPEG-2'
    case 'vp9':
      return 'VP9'
    default:
      return video.codec || video.codecName.toUpperCase()
  }
}

const hdrNames: Record<HdrType, string> = {
  None: '',
  Pq: 'PQ',
  Hdr10: 'HDR10',
  Hdr10Plus: 'HDR10+',
  Hlg: 'HLG',
  DolbyVision: 'Dolby Vision',
  DolbyVisionHdr10: 'Dolby Vision · HDR10',
  DolbyVisionHdr10Plus: 'Dolby Vision · HDR10+',
  DolbyVisionHlg: 'Dolby Vision · HLG',
  DolbyVisionSdr: 'Dolby Vision',
}

/** "HEVC · 10-bit · 2160p · Dolby Vision · HDR10". */
export function videoLine(video: MediaVideo): string {
  return [
    videoFormat(video),
    video.bitDepth > 0 ? `${video.bitDepth}-bit` : null,
    video.resolution > 0 ? `${video.resolution}p` : `${video.width}×${video.height}`,
    hdrNames[video.hdr] || null,
    video.is3D ? '3D' : null,
  ]
    .filter(Boolean)
    .join(' · ')
}
