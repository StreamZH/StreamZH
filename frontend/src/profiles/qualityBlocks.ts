import type { QualityProfileItem } from '../api/profiles'

/** A quality on its own, or a group of qualities that rank equally (TRaSH's "Merged QPs"). Items are worst first. */
export interface QualityBlock {
  key: string
  group: string | null
  items: QualityProfileItem[]
}

export function toBlocks(items: QualityProfileItem[]): QualityBlock[] {
  const blocks: QualityBlock[] = []
  for (const item of items) {
    const last = blocks[blocks.length - 1]
    if (item.group && last?.group === item.group) last.items.push(item)
    else blocks.push({ key: item.group ? `g:${item.group}` : `q:${item.qualityId}`, group: item.group ?? null, items: [item] })
  }
  return blocks
}

export const fromBlocks = (blocks: QualityBlock[]) => blocks.flatMap((b) => b.items)

/** The quality id that stands for a block in the cutoff select: its best quality. */
export const blockId = (b: QualityBlock) => b.items[b.items.length - 1].qualityId

export const blockName = (b: QualityBlock) => b.group ?? b.items[0].name

/** Keeps the cutoff on an allowed block: falls back to the best allowed one. */
export function validCutoff(items: QualityProfileItem[], cutoffQualityId: number): number {
  const blocks = toBlocks(items)
  const current = blocks.find((b) => b.items.some((i) => i.qualityId === cutoffQualityId))
  if (current && current.items.some((i) => i.allowed)) return cutoffQualityId
  const best = [...blocks].reverse().find((b) => b.items.some((i) => i.allowed))
  return best ? blockId(best) : 0
}
