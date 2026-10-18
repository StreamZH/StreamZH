import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useRef, useState } from 'react'
import { ApiError } from '../api/client'
import { customFormatKeys, customFormatsApi, type CustomFormat, type FormatMediaType } from '../api/customFormats'
import { profileKeys } from '../api/profiles'
import Icon from '../components/Icon'
import Loader from '../components/Loader'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import styles from './CustomFormats.module.css'

/** Paste (or open) a Radarr/Sonarr export or TRaSH Guides JSON: one format or a list. */
export function ImportDialog({ defaultType, onClose }: { defaultType: FormatMediaType; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [mediaType, setMediaType] = useState<FormatMediaType>(defaultType)
  const [json, setJson] = useState('')
  const file = useRef<HTMLInputElement>(null)

  const run = useMutation({
    mutationFn: () => customFormatsApi.import(mediaType, json),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: customFormatKeys.all })
      await queryClient.invalidateQueries({ queryKey: profileKeys.profiles })
    },
  })
  const fieldErrors = run.error instanceof ApiError ? run.error.fieldErrors : {}
  const jsonError = fieldErrors.json?.[0] ?? (run.error && !fieldErrors.mediaType ? run.error.message : null)
  const looksValid = (() => {
    if (!json.trim()) return null
    try {
      JSON.parse(json)
      return true
    } catch {
      return false
    }
  })()

  return (
    <Modal title="Import custom formats" onClose={onClose}>
      {run.isSuccess ? (
        <div className={styles.importDone}>
          <p className={ui.infoBox}>
            <Icon name="check" size={15} />
            {run.data.length} format{run.data.length === 1 ? '' : 's'} imported. Give them a score in a profile under Settings → Profiles.
          </p>
          <ul className={styles.importList}>
            {run.data.map((f) => (
              <li key={f.id}>
                <Icon name="tag" size={13} />
                {f.name}
                {f.trashId && <span className={styles.trash}>TRaSH</span>}
              </li>
            ))}
          </ul>
          <div className={styles.dialogFoot}>
            <span className={styles.spacer} />
            <button className={ui.primary} type="button" onClick={onClose}>
              Done
            </button>
          </div>
        </div>
      ) : (
        <form
          className={styles.editor}
          onSubmit={(e) => {
            e.preventDefault()
            run.mutate()
          }}
        >
          <p className={styles.dialogText}>
            Paste the JSON of one format or a list: a Radarr or Sonarr export, or a file from the TRaSH Guides. Formats with the same name or TRaSH id
            are updated.
          </p>
          <div className={styles.importBar}>
            <div className={ui.seg} role="group" aria-label="Formats for">
              {(['movie', 'series'] as const).map((t) => (
                <button key={t} type="button" aria-pressed={mediaType === t} onClick={() => setMediaType(t)}>
                  <Icon name={t === 'movie' ? 'film' : 'tv'} size={14} />
                  {t === 'movie' ? 'Movies (Radarr)' : 'Series (Sonarr)'}
                </button>
              ))}
            </div>
            <button type="button" className={`${ui.button} ${ui.sm}`} onClick={() => file.current?.click()}>
              <Icon name="file" size={14} />
              Open file
            </button>
            <input
              ref={file}
              type="file"
              accept=".json,application/json"
              hidden
              onChange={async (e) => {
                const f = e.target.files?.[0]
                if (f) setJson(await f.text())
                e.target.value = ''
              }}
            />
          </div>
          <textarea
            className={`${ui.input} ${ui.mono} ${styles.jsonArea}`}
            value={json}
            onChange={(e) => setJson(e.target.value)}
            placeholder={'{\n  "name": "German DL",\n  "specifications": [ … ]\n}'}
            spellCheck={false}
            aria-label="JSON"
            autoFocus
          />
          {looksValid === false && !jsonError && <small className={styles.regexWarn}>This does not look like valid JSON yet.</small>}
          {jsonError && <p className={ui.errBox}>{jsonError}</p>}
          <div className={styles.dialogFoot}>
            <span className={styles.spacer} />
            <button className={ui.button} type="button" onClick={onClose}>
              Cancel
            </button>
            <button className={ui.primary} type="submit" disabled={!json.trim() || run.isPending}>
              <Icon name="upload" size={15} />
              {run.isPending ? 'Importing…' : 'Import'}
            </button>
          </div>
        </form>
      )}
    </Modal>
  )
}

/** The Radarr/Sonarr-compatible JSON of one format, to copy. */
export function ExportDialog({ format, onClose }: { format: CustomFormat; onClose: () => void }) {
  const exported = useQuery({ queryKey: ['custom-format-export', format.id], queryFn: () => customFormatsApi.export(format.id), gcTime: 0 })
  const [copied, setCopied] = useState(false)
  const area = useRef<HTMLTextAreaElement>(null)
  const text = exported.data ? JSON.stringify(exported.data, null, 2) : ''

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(text)
    } catch {
      // No clipboard (plain http): select the text so Ctrl+C works.
      area.current?.select()
      document.execCommand?.('copy')
    }
    setCopied(true)
    window.setTimeout(() => setCopied(false), 2000)
  }

  return (
    <Modal title={`Export ${format.name}`} onClose={onClose}>
      <p className={styles.dialogText}>Radarr and Sonarr can import this JSON as it is.</p>
      {exported.isPending && <Loader text="Loading…" />}
      {exported.isError && <p className={ui.errBox}>{exported.error.message}</p>}
      {exported.data !== undefined && (
        <textarea ref={area} className={`${ui.input} ${ui.mono} ${styles.jsonArea}`} value={text} readOnly aria-label="JSON" spellCheck={false} />
      )}
      <div className={styles.dialogFoot}>
        <span className={styles.spacer} />
        <button className={ui.button} type="button" onClick={onClose}>
          Close
        </button>
        <button className={ui.primary} type="button" onClick={() => void copy()} disabled={!text}>
          <Icon name={copied ? 'check' : 'copy'} size={15} />
          {copied ? 'Copied' : 'Copy JSON'}
        </button>
      </div>
    </Modal>
  )
}
