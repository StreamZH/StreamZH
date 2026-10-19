import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useId, useState, type ChangeEvent } from 'react'
import { ApiError } from '../api/client'
import { discordApi, discordKeys, type RequestrrImportResult } from '../api/discord'
import Icon from '../components/Icon'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import styles from './Discord.module.css'

/** "100…20, 100…21" with known ids replaced by "#requests, #general". */
function named(value: string, names: Record<string, string>): string {
  return value
    .split(', ')
    .map((part) => names[part] ?? part)
    .join(', ')
}

/** Import from Requestrr: paste or choose config/settings.json → preview of the changes → apply. */
export default function RequestrrImportDialog({ onClose, names = {} }: { onClose: () => void; names?: Record<string, string> }) {
  const queryClient = useQueryClient()
  const id = useId()
  const [text, setText] = useState('')
  const [fileName, setFileName] = useState<string | null>(null)
  const [takeToken, setTakeToken] = useState(false)
  const [preview, setPreview] = useState<RequestrrImportResult | null>(null)

  const previewMutation = useMutation({
    mutationFn: () => discordApi.importRequestrr(text, takeToken, false),
    onSuccess: setPreview,
  })
  const apply = useMutation({
    mutationFn: () => discordApi.importRequestrr(text, takeToken, true),
    onSuccess: (result) => {
      setPreview(result)
      if (result.settings) queryClient.setQueryData(discordKeys.settings, result.settings)
    },
  })

  const error = (previewMutation.error ?? apply.error) as Error | null
  const errorText = error instanceof ApiError ? (error.fieldErrors.settings?.join(' ') ?? error.message) : error?.message

  const onFile = async (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    if (!file) return
    setFileName(file.name)
    setText(await file.text())
    setPreview(null)
  }

  const applied = preview?.applied === true

  return (
    <Modal title="Import from Requestrr" onClose={onClose}>
      {!preview && (
        <div className={styles.importBody}>
          <p className={styles.importLead}>
            StreamZH takes over the request channels, the roles that may request, the notification settings and the TV season rule from Requestrr’s{' '}
            <code>config/settings.json</code>. Nothing is saved before you confirm the preview.
          </p>
          <label className={ui.field} htmlFor={`${id}-json`}>
            <span>settings.json</span>
            <textarea
              id={`${id}-json`}
              className={`${ui.input} ${ui.mono} ${styles.importText}`}
              spellCheck={false}
              placeholder='{ "ChatClients": { "Discord": { … } } … }'
              value={text}
              onChange={(e) => {
                setText(e.target.value)
                setFileName(null)
                setPreview(null)
              }}
            />
          </label>
          <div className={styles.importFile}>
            <label className={`${ui.button} ${ui.sm}`}>
              <Icon name="upload" size={14} />
              Choose file
              <input type="file" accept=".json,application/json" className={styles.hiddenFile} onChange={onFile} />
            </label>
            {fileName && <span className={ui.muted}>{fileName}</span>}
          </div>
          <label className={ui.check}>
            <input type="checkbox" checked={takeToken} onChange={(e) => setTakeToken(e.target.checked)} />
            <span>
              Take over the bot token <small className={ui.muted}>(stop Requestrr first: two programs with one token both answer every command)</small>
            </span>
          </label>
          {errorText && <p className={ui.errBox}>{errorText}</p>}
          <div className={styles.importActions}>
            <button type="button" className={ui.ghost} onClick={onClose}>
              Cancel
            </button>
            <button type="button" className={ui.primary} disabled={!text.trim() || previewMutation.isPending} onClick={() => previewMutation.mutate()}>
              {previewMutation.isPending ? 'Reading…' : 'Preview'}
            </button>
          </div>
        </div>
      )}

      {preview && (
        <div className={styles.importBody}>
          <p className={styles.importLead}>
            {applied ? (
              <span className={styles.importDone}>
                <Icon name="check" size={16} /> Imported. The bot restarts with the new settings.
              </span>
            ) : preview.rows.length === 0 ? (
              'Nothing to change: StreamZH already has these settings.'
            ) : (
              <>
                {preview.rows.length} setting{preview.rows.length === 1 ? '' : 's'} will change
                {preview.version ? <span className={ui.muted}> · Requestrr {preview.version}</span> : null}
              </>
            )}
          </p>
          {preview.rows.length > 0 && (
            <div className={styles.importTableWrap}>
              <table className={ui.table}>
                <thead>
                  <tr>
                    <th>Setting</th>
                    <th>Now</th>
                    <th>From Requestrr</th>
                  </tr>
                </thead>
                <tbody>
                  {preview.rows.map((row) => (
                    <tr key={row.field}>
                      <td>{row.label}</td>
                      <td className={styles.importValue}>{named(row.current, names)}</td>
                      <td className={styles.importValue}>{named(row.imported, names)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          {preview.warnings.length > 0 && (
            <ul className={styles.importWarnings}>
              {preview.warnings.map((w) => (
                <li key={w}>
                  <Icon name="info" size={14} />
                  <span>{w}</span>
                </li>
              ))}
            </ul>
          )}
          {errorText && <p className={ui.errBox}>{errorText}</p>}
          <div className={styles.importActions}>
            {applied ? (
              <button type="button" className={ui.primary} onClick={onClose}>
                Close
              </button>
            ) : (
              <>
                <button type="button" className={ui.ghost} onClick={() => setPreview(null)} disabled={apply.isPending}>
                  Back
                </button>
                <button type="button" className={ui.primary} onClick={() => apply.mutate()} disabled={apply.isPending || preview.rows.length === 0}>
                  <Icon name="check" size={15} />
                  {apply.isPending ? 'Importing…' : 'Apply'}
                </button>
              </>
            )}
          </div>
        </div>
      )}
    </Modal>
  )
}
