import type { DownloadItem } from '../api/downloads'

/** Totals of the download queue for the Activity header and the top bar (Usenet and torrents together). */
export interface QueueTotals {
  /** Items in the queue. */
  count: number
  torrents: number
  /** Items that can move now: not paused, and not Usenet while the Usenet queue is paused. */
  moving: number
  /** Download speed of the moving items, bytes per second. */
  speed: number
  /** Bytes the moving items still have to download. */
  remaining: number
  /** Seconds until the moving items are downloaded at the current speed; null when nothing downloads. */
  eta: number | null
  /** The Usenet queue is paused (Pause all) while torrents keep running in their client. */
  usenetPaused: boolean
}

const finished: DownloadItem['status'][] = ['completed', 'failed']

/**
 * `items` with the live progress applied (withLive). Pause all pauses the Usenet queue only (torrents are paused in their
 * client), so paused Usenet items neither count for the speed nor for the time left, and torrents still do.
 */
export function queueTotals(items: DownloadItem[], usenetPaused: boolean): QueueTotals {
  let moving = 0
  let speed = 0
  let remaining = 0
  let torrents = 0
  for (const item of items) {
    const torrent = item.protocol === 'torrent'
    if (torrent) torrents++
    if (item.status === 'paused' || finished.includes(item.status) || (usenetPaused && !torrent)) continue
    moving++
    speed += Math.max(0, item.speedBps || 0)
    if (item.status === 'downloading' || item.status === 'queued') remaining += Math.max(0, item.totalBytes - item.downloadedBytes)
  }
  return {
    count: items.length,
    torrents,
    moving,
    speed,
    remaining,
    eta: speed > 0 && remaining > 0 ? remaining / speed : null,
    usenetPaused,
  }
}
