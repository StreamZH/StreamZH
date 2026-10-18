import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { queryKeys, type MediaType } from '../api/library'
import { organizeApi, type MassEditChanges } from '../api/organize'
import { useProfileChoices } from '../api/profiles'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import styles from './MassEdit.module.css'
import OrganizeDialog from './OrganizeDialog'
import RemoveDialog, { type RemoveTarget } from './RemoveDialog'
import RootFolderDialog from './RootFolderDialog'

type Dialog = 'organize' | 'root' | 'remove' | null

/** The sticky bar of select mode: what to do with the selected movies or series. */
export default function MassEditBar({
  type,
  selected,
  onClear,
  onDone,
  selectAll,
  onSelectNone,
}: {
  type: MediaType
  selected: RemoveTarget[]
  onClear: () => void
  onDone: () => void
  /** "All (12)": adds every shown item to the selection. */
  selectAll?: { label: string; disabled: boolean; onClick: () => void }
  onSelectNone?: () => void
}) {
  const queryClient = useQueryClient()
  const { profiles } = useProfileChoices()
  const [dialog, setDialog] = useState<Dialog>(null)
  const [message, setMessage] = useState<string | null>(null)
  const ids = selected.map((s) => s.id)
  const count = selected.length
  const noun = type === 'Movie' ? (count === 1 ? 'movie' : 'movies') : 'series'

  const edit = useMutation({
    mutationFn: (changes: MassEditChanges) => organizeApi.edit(type, ids, changes),
    onSuccess: async (result) => {
      setMessage(`${result.updated} changed`)
      await queryClient.invalidateQueries({ queryKey: type === 'Movie' ? queryKeys.movies : queryKeys.series })
    },
    onError: () => setMessage(null),
  })

  const disabled = count === 0 || edit.isPending
  const btn = `${ui.button} ${ui.sm}`
  return (
    <>
      <div className={styles.bar} role="region" aria-label="Selected items">
        <div className={styles.barCount}>
          <span className={ui.num}>
            <strong>{count}</strong> {noun} selected
          </span>
          {selectAll && (
            <button type="button" className={`${ui.ghost} ${ui.sm}`} disabled={selectAll.disabled} onClick={selectAll.onClick}>
              {selectAll.label}
            </button>
          )}
          {onSelectNone && (
            <button type="button" className={`${ui.ghost} ${ui.sm}`} disabled={count === 0} onClick={onSelectNone}>
              None
            </button>
          )}
          {message && !edit.isPending && (
            <span className={styles.barMessage} role="status">
              <Icon name="check" size={13} /> {message}
            </span>
          )}
          {edit.error && <span className={ui.error}>{edit.error.message}</span>}
        </div>
        <div className={styles.barActions}>
          <button type="button" className={btn} disabled={disabled} onClick={() => edit.mutate({ monitored: true })}>
            <Icon name="eye" size={14} />
            Monitor
          </button>
          <button type="button" className={btn} disabled={disabled} onClick={() => edit.mutate({ monitored: false })}>
            <Icon name="eyeoff" size={14} />
            Unmonitor
          </button>
          <select
            className={`${ui.select} ${styles.barSelect}`}
            value=""
            disabled={disabled || profiles.length === 0}
            aria-label="Set quality profile"
            onChange={(e) => e.target.value && edit.mutate({ qualityProfileId: Number(e.target.value) })}
          >
            <option value="">Quality profile…</option>
            {profiles.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name}
              </option>
            ))}
          </select>
          <button type="button" className={btn} disabled={disabled} onClick={() => setDialog('root')}>
            <Icon name="folder" size={14} />
            Root folder…
          </button>
          <button type="button" className={`${ui.primary} ${ui.sm}`} disabled={disabled} onClick={() => setDialog('organize')}>
            <Icon name="rename" size={14} />
            Organize…
          </button>
          <button type="button" className={`${ui.danger} ${ui.sm}`} disabled={disabled} onClick={() => setDialog('remove')}>
            <Icon name="trash" size={14} />
            Remove…
          </button>
          <button type="button" className={`${ui.ghost} ${ui.sm} ${styles.barDone}`} onClick={onClear}>
            Done
          </button>
        </div>
      </div>
      {dialog === 'organize' && <OrganizeDialog type={type} ids={ids} onClose={() => setDialog(null)} />}
      {dialog === 'root' && <RootFolderDialog type={type} ids={ids} onClose={() => setDialog(null)} />}
      {dialog === 'remove' && (
        <RemoveDialog
          type={type}
          items={selected}
          onClose={() => setDialog(null)}
          onRemoved={() => {
            setDialog(null)
            onDone()
          }}
        />
      )}
    </>
  )
}
