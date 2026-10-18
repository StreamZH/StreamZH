import { useMemo, useState } from 'react'
import type { PreviewResult, PreviewRow } from '../../api/renamer'
import Icon from '../../components/Icon'
import ui from '../../components/ui.module.css'
import { formatBytes } from '../../lib/format'
import styles from './Renamer.module.css'

type View = 'changes' | 'problems' | 'all'

const statusLabel: Record<PreviewRow['status'], string> = {
  unchanged: 'Unchanged',
  rename: 'Will rename',
  conflict: 'Conflict',
  error: 'Error',
  excluded: 'Skipped',
}

const problemLabel: Record<string, string> = {
  duplicate: 'Duplicate name',
  exists: 'Name taken',
  invalid: 'Invalid name',
  tooLong: 'Too long',
  empty: 'Empty name',
  libraryFolder: 'Library folder',
  follows: 'Video stays',
  error: 'Error',
}

/** Splits two names into the common start, the changed middle and the common end, for highlighting. */
function diff(a: string, b: string) {
  let start = 0
  while (start < a.length && start < b.length && a[start] === b[start]) start++
  let end = 0
  while (end < a.length - start && end < b.length - start && a[a.length - 1 - end] === b[b.length - 1 - end]) end++
  return {
    old: [a.slice(0, start), a.slice(start, a.length - end), a.slice(a.length - end)],
    new: [b.slice(0, start), b.slice(start, b.length - end), b.slice(b.length - end)],
  }
}

function Name({ parts, kind }: { parts: string[]; kind: 'old' | 'new' }) {
  return (
    <>
      {parts[0]}
      {parts[1] && <mark className={kind === 'old' ? styles.removed : styles.added}>{parts[1]}</mark>}
      {parts[2]}
    </>
  )
}

function MediaChip({ row }: { row: PreviewRow }) {
  const m = row.media
  if (!m) return null
  const numbers =
    m.kind === 'episode' && m.season != null && m.episodes.length > 0
      ? `S${String(m.season).padStart(2, '0')}${m.episodes.map((e) => `E${String(e).padStart(2, '0')}`).join('')}`
      : m.year > 0
        ? String(m.year)
        : null
  const source = m.source === 'library' ? 'Library' : m.source === 'tmdb' ? 'TMDB' : 'From the name'
  return (
    <span className={`${styles.mediaChip} ${m.source === 'name' ? styles.mediaChipWeak : ''}`} title={m.note ?? undefined}>
      <span>{m.title}</span>
      {numbers && <span className={ui.mono}>{numbers}</span>}
      {m.episodeTitle && <span className={styles.mediaTitle}>{m.episodeTitle}</span>}
      <span className={styles.mediaSource}>{source}</span>
      {m.note && <span className={styles.mediaNote}>{m.note}</span>}
    </span>
  )
}

/** Old name → new name for every file, grouped by folder, with the status and a switch per row. Nothing happens here. */
export default function PreviewTable({
  preview,
  excluded,
  onToggle,
  onToggleAll,
  nothingText = 'Nothing changes yet. Add rules, or show all files.',
}: {
  preview: PreviewResult
  /** Shown when no row changes. */
  nothingText?: string
  excluded: Set<string>
  onToggle: (key: string, include: boolean) => void
  onToggleAll: (keys: string[], include: boolean) => void
}) {
  const [view, setView] = useState<View>('changes')
  const counts = preview.counts
  const problems = counts.conflict + counts.error

  const rows = useMemo(
    () =>
      preview.rows.filter((r) =>
        view === 'all' ? true : view === 'problems' ? r.status === 'conflict' || r.status === 'error' : r.status !== 'unchanged',
      ),
    [preview.rows, view],
  )

  const groups = useMemo(() => {
    const map = new Map<string, PreviewRow[]>()
    for (const row of rows) {
      const list = map.get(row.folder) ?? []
      list.push(row)
      map.set(row.folder, list)
    }
    return [...map.entries()]
  }, [rows])

  const toggleable = rows.filter((r) => (r.status === 'rename' || r.status === 'excluded') && !r.companionOf)
  const allOn = toggleable.length > 0 && toggleable.every((r) => !excluded.has(r.key))

  return (
    <div className={styles.preview}>
      <div className={styles.previewBar}>
        <div className={styles.counts}>
          <span className={`${styles.count} ${styles.countRename}`}>
            <strong>{counts.rename}</strong> will rename
          </span>
          <span className={styles.count}>
            <strong>{counts.unchanged}</strong> unchanged
          </span>
          {counts.conflict > 0 && (
            <span className={`${styles.count} ${styles.countConflict}`}>
              <strong>{counts.conflict}</strong> conflict{counts.conflict === 1 ? '' : 's'}
            </span>
          )}
          {counts.error > 0 && (
            <span className={`${styles.count} ${styles.countError}`}>
              <strong>{counts.error}</strong> error{counts.error === 1 ? '' : 's'}
            </span>
          )}
          {counts.excluded > 0 && (
            <span className={styles.count}>
              <strong>{counts.excluded}</strong> skipped
            </span>
          )}
        </div>
        <div className={styles.segmented} role="tablist" aria-label="Rows">
          {(
            [
              ['changes', 'Changes'],
              ['problems', `Problems${problems ? ` (${problems})` : ''}`],
              ['all', 'All'],
            ] as const
          ).map(([value, label]) => (
            <button key={value} type="button" role="tab" aria-selected={view === value} className={view === value ? styles.segmentOn : ''} onClick={() => setView(value)}>
              {label}
            </button>
          ))}
        </div>
      </div>

      {preview.truncated && <p className={styles.notice}>Only the first 10,000 files are shown. Narrow the filter or the subfolder depth.</p>}

      {rows.length === 0 ? (
        <p className={styles.emptyRows}>
          {preview.rows.length === 0
            ? 'No files match the filter.'
            : view === 'problems'
              ? 'No problems.'
              : nothingText}
        </p>
      ) : (
        <div className={styles.table} role="table" aria-label="Preview">
          <div className={styles.tableHead} role="row">
            <span role="columnheader">
              {toggleable.length > 0 && (
                <input
                  type="checkbox"
                  checked={allOn}
                  aria-label="Rename all"
                  onChange={(e) => onToggleAll(toggleable.map((r) => r.key), e.target.checked)}
                />
              )}
            </span>
            <span role="columnheader">Current name</span>
            <span role="columnheader" aria-hidden="true" />
            <span role="columnheader">New name</span>
            <span role="columnheader">Status</span>
          </div>
          {groups.map(([folder, list]) => (
            <div key={folder || '.'} role="rowgroup">
              <div className={styles.folderRow} role="row">
                <Icon name="folder" size={14} />
                <span role="cell" className={styles.folderName}>
                  {folder === '' ? preview.folder : folder}
                </span>
              </div>
              {list.map((row) => {
                const parts = diff(row.oldName, row.newName)
                const moved = row.newFolder !== row.folder
                const canToggle = (row.status === 'rename' || row.status === 'excluded') && !row.companionOf
                const statusClass =
                  row.status === 'rename'
                    ? styles.statusRename
                    : row.status === 'conflict'
                      ? styles.statusConflict
                      : row.status === 'error'
                        ? styles.statusError
                        : styles.statusIdle
                const statusPill =
                  row.status === 'rename' ? ui.pillOk : row.status === 'conflict' ? ui.pillWarn : row.status === 'error' ? ui.pillErr : ''
                return (
                  <div key={row.key} role="row" className={`${styles.row} ${row.companionOf ? styles.companion : ''} ${row.status === 'excluded' ? styles.rowOff : ''}`}>
                    <span role="cell" className={styles.cellCheck}>
                      {canToggle && (
                        <input type="checkbox" checked={!excluded.has(row.key)} aria-label={`Rename ${row.oldName}`} onChange={(e) => onToggle(row.key, e.target.checked)} />
                      )}
                    </span>
                    <span role="cell" className={styles.cellOld}>
                      {row.isFolder && <span className={styles.folderTag}>Folder</span>}
                      {row.companionOf && <span className={styles.followTag}>follows</span>}
                      <span className={styles.nameText}>
                        <Name parts={parts.old} kind="old" />
                      </span>
                      {!row.isFolder && <span className={styles.size}>{formatBytes(row.size)}</span>}
                    </span>
                    <span role="cell" className={styles.arrow} aria-hidden="true">
                      <Icon name="right" size={14} />
                    </span>
                    <span role="cell" className={styles.cellNew}>
                      {moved && <span className={styles.movedTo}>{row.newFolder || preview.folder}/</span>}
                      <span className={styles.nameText}>{row.status === 'unchanged' ? <span className={ui.muted}>{row.newName}</span> : <Name parts={parts.new} kind="new" />}</span>
                      <MediaChip row={row} />
                    </span>
                    <span role="cell" className={`${styles.cellStatus} ${statusClass}`}>
                      <span className={`${ui.pill} ${statusPill} ${styles.statusLabel}`}>{row.problem ? problemLabel[row.problem] ?? statusLabel[row.status] : statusLabel[row.status]}</span>
                      {row.message && <small>{row.message}</small>}
                    </span>
                  </div>
                )
              })}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
