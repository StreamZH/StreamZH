import { useEffect, useRef, useState } from 'react'
import type { QualityProfileItem } from '../api/profiles'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import styles from './ProfileDialog.module.css'
import { blockName, fromBlocks, toBlocks, type QualityBlock } from './qualityBlocks'

/**
 * The qualities of a profile, best first. Groups show as one row with their qualities as chips. In advanced mode
 * qualities can be selected and grouped, and groups ungrouped.
 */
export default function QualityList({
  items,
  cutoffQualityId,
  canEdit,
  advanced,
  onChange,
}: {
  items: QualityProfileItem[]
  cutoffQualityId: number
  canEdit: boolean
  advanced: boolean
  onChange: (items: QualityProfileItem[]) => void
}) {
  const blocks = toBlocks(items)
  const shown = [...blocks].reverse()
  const [selected, setSelected] = useState<string[]>([])
  const [groupName, setGroupName] = useState('')
  const listRef = useRef<HTMLOListElement>(null)
  const pendingFocus = useRef<{ key: string; dir: 'up' | 'down' } | null>(null)

  // Keep keyboard focus on the move button after the row moved.
  useEffect(() => {
    const focus = pendingFocus.current
    pendingFocus.current = null
    if (!focus || !listRef.current) return
    const row = listRef.current.querySelector<HTMLElement>(`[data-block="${CSS.escape(focus.key)}"]`)
    const wanted = row?.querySelector<HTMLButtonElement>(`[data-move="${focus.dir}"]`)
    const other = row?.querySelector<HTMLButtonElement>(`[data-move="${focus.dir === 'up' ? 'down' : 'up'}"]`)
    ;(wanted && !wanted.disabled ? wanted : other)?.focus()
  }, [items])

  const toggle = (key: string, allowed: boolean) =>
    onChange(fromBlocks(blocks.map((b) => (b.key === key ? { ...b, items: b.items.map((i) => ({ ...i, allowed })) } : b))))

  /** Moves a block one step better (up) or worse (down). */
  const move = (key: string, dir: 'up' | 'down') => {
    const index = blocks.findIndex((b) => b.key === key)
    const target = dir === 'up' ? index + 1 : index - 1
    if (index < 0 || target < 0 || target >= blocks.length) return
    const next = [...blocks]
    ;[next[index], next[target]] = [next[target], next[index]]
    pendingFocus.current = { key, dir }
    onChange(fromBlocks(next))
  }

  const ungroup = (key: string) =>
    onChange(fromBlocks(blocks.map((b) => (b.key === key ? { ...b, items: b.items.map((i) => ({ ...i, group: null })) } : b))))

  const selectedBlocks = blocks.filter((b) => selected.includes(b.key))
  const selectedCount = selectedBlocks.reduce((n, b) => n + b.items.length, 0)
  const existingGroup = selectedBlocks.find((b) => b.group)?.group ?? null
  const names = new Set(blocks.filter((b) => b.group && !selected.includes(b.key)).map((b) => b.group))
  const newName = groupName.trim() || existingGroup || ''
  const nameTaken = names.has(newName)

  /** Merges the selected rows into one group, placed where the best of them was. */
  const group = () => {
    if (selectedBlocks.length < 2 || !newName || nameTaken) return
    const bestIndex = Math.max(...selectedBlocks.map((b) => blocks.indexOf(b)))
    const members = selectedBlocks.flatMap((b) => b.items)
    const allowed = members.some((i) => i.allowed)
    const merged: QualityBlock = { key: `g:${newName}`, group: newName, items: members.map((i) => ({ ...i, group: newName, allowed })) }
    const next: QualityBlock[] = []
    blocks.forEach((b, i) => {
      if (i === bestIndex) next.push(merged)
      else if (!selected.includes(b.key)) next.push(b)
    })
    setSelected([])
    setGroupName('')
    onChange(fromBlocks(next))
  }

  const cutoffBlock = blocks.find((b) => b.items.some((i) => i.qualityId === cutoffQualityId))

  return (
    <>
      <ol className={styles.qualities} ref={listRef} aria-label="Qualities, best first">
        {shown.map((block, index) => {
          const allowed = block.items.some((i) => i.allowed)
          const isCutoff = cutoffBlock?.key === block.key && allowed
          const name = blockName(block)
          return (
            <li key={block.key} data-block={block.key} className={`${allowed ? '' : styles.notAllowed} ${selected.includes(block.key) ? styles.picked : ''}`}>
              <label className={`${ui.check} ${styles.qualityName}`}>
                <input type="checkbox" checked={allowed} onChange={(e) => toggle(block.key, e.target.checked)} />
                <span>{name}</span>
              </label>
              {block.group && (
                <span className={styles.groupChips}>
                  {[...block.items].reverse().map((i) => (
                    <span key={i.qualityId} className={styles.groupChip}>
                      {i.name}
                    </span>
                  ))}
                </span>
              )}
              {isCutoff && <span className={`${ui.tag} ${styles.cutoffTag}`}>Cutoff</span>}
              {canEdit && (
                <span className={styles.moveButtons}>
                  {advanced && (
                    <button
                      type="button"
                      className={`${ui.ghost} ${ui.sm} ${ui.icon} ${styles.pick}`}
                      aria-pressed={selected.includes(block.key)}
                      onClick={() => setSelected((s) => (s.includes(block.key) ? s.filter((k) => k !== block.key) : [...s, block.key]))}
                      aria-label={`Select ${name} for grouping`}
                      title="Select for grouping"
                    >
                      <Icon name="layers" size={14} />
                    </button>
                  )}
                  {advanced && block.group && (
                    <button type="button" className={`${ui.ghost} ${ui.sm}`} onClick={() => ungroup(block.key)} title="Split the group into single qualities">
                      Ungroup
                    </button>
                  )}
                  <button
                    type="button"
                    className={`${ui.ghost} ${ui.sm} ${ui.icon}`}
                    data-move="up"
                    onClick={() => move(block.key, 'up')}
                    disabled={index === 0}
                    aria-label={`Move ${name} up`}
                  >
                    <Icon name="up" size={15} />
                  </button>
                  <button
                    type="button"
                    className={`${ui.ghost} ${ui.sm} ${ui.icon}`}
                    data-move="down"
                    onClick={() => move(block.key, 'down')}
                    disabled={index === shown.length - 1}
                    aria-label={`Move ${name} down`}
                  >
                    <Icon name="down" size={15} />
                  </button>
                </span>
              )}
            </li>
          )
        })}
      </ol>
      {advanced && canEdit && selectedBlocks.length > 0 && (
        <div className={styles.groupBar}>
          <span className={styles.groupBarText}>
            {selectedCount} qualit{selectedCount === 1 ? 'y' : 'ies'} selected
          </span>
          {selectedBlocks.length >= 2 ? (
            <>
              <input
                className={`${ui.input} ${styles.groupInput}`}
                value={groupName}
                placeholder={existingGroup ?? 'Group name, e.g. WEB 1080p'}
                onChange={(e) => setGroupName(e.target.value)}
                aria-label="Group name"
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    e.preventDefault()
                    group()
                  }
                }}
              />
              <button type="button" className={`${ui.primary} ${ui.sm}`} onClick={group} disabled={!newName || nameTaken}>
                <Icon name="layers" size={14} />
                Group
              </button>
              {nameTaken && <small className={ui.error}>Another group has this name.</small>}
            </>
          ) : (
            <span className={ui.muted}>Select one more to group them.</span>
          )}
          <button type="button" className={`${ui.ghost} ${ui.sm}`} onClick={() => setSelected([])}>
            Clear
          </button>
        </div>
      )}
    </>
  )
}
