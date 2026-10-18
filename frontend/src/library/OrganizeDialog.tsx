import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { Link } from 'react-router'
import type { MediaType } from '../api/library'
import { organizeApi, organizeKeys, type OrganizeFileKind, type OrganizeItem } from '../api/organize'
import Icon from '../components/Icon'
import Loader, { Skeleton } from '../components/Loader'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import styles from './MassEdit.module.css'
import { RunProgress, RunResult, UndoButton } from './RunProgress'
import { useOrganizeStatus } from './useOrganizeStatus'

const kindLabels: Record<OrganizeFileKind, string> = { video: 'Video', subtitle: 'Subtitle', metadata: 'NFO / art', extra: 'Extra' }

/**
 * Organize (Radarr's and Sonarr's Organize, plus the folder): previews the new folder and file names from Settings →
 * Media Management for the chosen movies or series, then renames them as a job with progress. Undo stays possible.
 */
export default function OrganizeDialog({ type, ids, onClose }: { type: MediaType; ids: number[]; onClose: () => void }) {
  const queryClient = useQueryClient()
  const preview = useQuery({
    queryKey: organizeKeys.preview(type, ids),
    queryFn: () => organizeApi.preview(type, ids),
    staleTime: 0,
    gcTime: 0,
  })
  const status = useOrganizeStatus()
  const [showUnchanged, setShowUnchanged] = useState(false)
  const [skipped, setSkipped] = useState<Set<number>>(new Set())
  const [startedAfter, setStartedAfter] = useState<string | null | undefined>(undefined)

  const items = useMemo(() => preview.data ?? [], [preview.data])
  const changing = items.filter((i) => i.hasChanges)
  const ready = changing.filter((i) => i.canApply)
  const conflicts = changing.filter((i) => !i.canApply)
  const unchanged = items.length - changing.length
  const chosen = ready.filter((i) => !skipped.has(i.id))
  const shown = showUnchanged ? items : changing
  const noun = (n: number) => (type === 'Movie' ? (n === 1 ? 'movie' : 'movies') : 'series')

  const apply = useMutation({
    mutationFn: () => organizeApi.apply(type, chosen.map((i) => i.id)),
    onMutate: () => setStartedAfter(status.data?.last?.finished ?? null),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: organizeKeys.status }),
  })

  const started = startedAfter !== undefined && apply.isSuccess
  const run = status.data?.running
  const last = status.data?.last
  const finished = started && !run && last && last.finished !== startedAfter ? last : null

  if (started) {
    return (
      <Modal title={finished ? 'Organize finished' : 'Organizing…'} onClose={onClose}>
        {run && <RunProgress run={run} />}
        {!run && !finished && <Loader text="Starting…" />}
        {finished && <RunResult result={finished} />}
        {finished && finished.runId != null && (
          <p className={styles.text}>Not what you wanted? Undo renames everything back, for the next 24 hours.</p>
        )}
        <div className={styles.actions}>
          {finished && finished.runId != null ? <UndoButton status={status.data} label="Undo this organize" /> : <span />}
          <button type="button" className={ui.primary} onClick={onClose}>
            {finished ? 'Done' : 'Close'}
          </button>
        </div>
        {!finished && <p className={styles.hint}>You can close this window; the progress also shows in the status window.</p>}
      </Modal>
    )
  }

  return (
    <Modal title={`Organize ${ids.length} ${noun(ids.length)}`} onClose={onClose}>
      {preview.isPending && (
        <>
          <p className={styles.text}>Working out the new names…</p>
          <Skeleton rows={4} />
        </>
      )}
      {preview.isError && <p className={ui.error}>{preview.error.message}</p>}
      {preview.isSuccess && (
        <>
          <p className={styles.text}>
            Folders and files are renamed to the naming scheme in{' '}
            <Link to="/settings/media-management" onClick={onClose}>
              Settings → Media Management
            </Link>
            . Subtitles, NFO and artwork next to a video get the new name too.
          </p>
          <div className={styles.summary}>
            <span className={`${ui.badge} ${ui.ok} ${ui.num}`}>{ready.length} ready</span>
            {conflicts.length > 0 && <span className={`${ui.badge} ${ui.missing} ${ui.num}`}>{conflicts.length} with a conflict</span>}
            {unchanged > 0 && (
              <label className={ui.check}>
                <input type="checkbox" checked={showUnchanged} onChange={(e) => setShowUnchanged(e.target.checked)} />
                Show {unchanged} already named right
              </label>
            )}
          </div>
          {changing.length === 0 && !showUnchanged && (
            <div className={ui.empty}>
              <Icon name="check" size={22} />
              <span>Everything already follows the naming scheme. Nothing to rename.</span>
            </div>
          )}
          {shown.length > 0 && (
            <ul className={styles.previewList}>
              {shown.map((item) => (
                <PreviewItem
                  key={item.id}
                  item={item}
                  checked={item.canApply && !skipped.has(item.id)}
                  onToggle={(on) =>
                    setSkipped((s) => {
                      const next = new Set(s)
                      if (on) next.delete(item.id)
                      else next.add(item.id)
                      return next
                    })
                  }
                />
              ))}
            </ul>
          )}
        </>
      )}
      {apply.error && (
        <p className={ui.error} role="alert">
          {apply.error.message}
        </p>
      )}
      <div className={styles.actions}>
        <button type="button" className={ui.button} onClick={onClose}>
          Cancel
        </button>
        <button type="button" className={ui.primary} disabled={chosen.length === 0 || apply.isPending || !!run} onClick={() => apply.mutate()}>
          <Icon name="rename" size={15} />
          {run ? 'Another run is busy…' : `Organize ${chosen.length} ${noun(chosen.length)}`}
        </button>
      </div>
    </Modal>
  )
}

function PreviewItem({ item, checked, onToggle }: { item: OrganizeItem; checked: boolean; onToggle: (on: boolean) => void }) {
  const name = (path: string) => path.split(/[\\/]/).pop() ?? path
  const others = item.otherFileCount - item.otherFiles.length
  return (
    <li className={`${styles.previewItem} ${item.conflicts.length > 0 ? styles.conflict : ''} ${!item.hasChanges ? styles.unchanged : ''}`}>
      <input
        type="checkbox"
        checked={checked}
        disabled={!item.canApply}
        aria-label={`Organize ${item.title}`}
        onChange={(e) => onToggle(e.target.checked)}
      />
      <div className={styles.previewBody}>
        <span className={styles.previewTitle}>
          {item.title}
          {item.year > 0 && <span className={ui.muted}> ({item.year})</span>}
          {!item.hasChanges && <span className={ui.muted}> · already named right</span>}
        </span>
        {item.folderChanged && (
          <span className={styles.paths}>
            <span className={styles.kind}>Folder</span>
            <span className={styles.old}>{name(item.existingFolder)}</span>
            <span className={styles.new}>{name(item.newFolder)}</span>
          </span>
        )}
        {item.files.map((f) => (
          <span key={f.existingPath} className={styles.paths}>
            <span className={styles.kind}>{kindLabels[f.kind]}</span>
            <span className={styles.old}>{f.existingPath}</span>
            <span className={styles.new}>{f.newPath}</span>
          </span>
        ))}
        {item.hasChanges && item.otherFileCount > 0 && (
          <span className={styles.note}>
            Moves along unchanged: {item.otherFiles.join(', ')}
            {others > 0 ? ` and ${others} more` : ''}
          </span>
        )}
        {item.notes.map((n) => (
          <span key={n} className={styles.note}>
            {n}
          </span>
        ))}
        {item.conflicts.map((c) => (
          <span key={c} className={styles.conflictText}>
            <Icon name="alert" size={14} />
            {c}
          </span>
        ))}
      </div>
    </li>
  )
}
