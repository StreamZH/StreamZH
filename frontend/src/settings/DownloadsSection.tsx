import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { ApiError } from '../api/client'
import { releaseKeys, releasesApi, type DownloadClient, type DownloadSettingsChange } from '../api/releases'
import FolderPicker from '../components/FolderPicker'
import ui from '../components/ui.module.css'
import form from '../indexers/Form.module.css'
import { Advanced, AdvancedHint } from '../lib/Advanced'
import { advancedLabel, setAdvanced, useAdvanced } from '../lib/advancedMode'
import page from '../pages/Page.module.css'
import styles from '../pages/Settings.module.css'

type FolderField = 'nzbFolder' | 'incompleteFolder' | 'completeFolder'

interface DownloadDraft {
  client?: DownloadClient
  nzbFolder?: string
  incompleteFolder?: string
  completeFolder?: string
  speedLimit?: string
  minFreeSpace?: string
  keepFiles?: boolean
  abortHopeless?: boolean
  articleTries?: string
}

/** Expert settings of the Downloads card; a save error on one of them switches advanced mode on. */
const advancedDownloadFields = ['incompleteFolder', 'completeFolder', 'minFreeSpaceGb', 'keepFiles', 'abortHopeless', 'articleTries']

function SwitchField({
  label,
  checked,
  help,
  onChange,
}: {
  label: string
  checked: boolean
  help: string
  onChange: (checked: boolean) => void
}) {
  return (
    <div className={styles.field}>
      <span className={advancedLabel}>{label}</span>
      <label className={form.switch}>
        <input type="checkbox" role="switch" checked={checked} onChange={(e) => onChange(e.target.checked)} aria-label={label} />
        <span className={form.track} aria-hidden="true" />
        <span>{checked ? 'On' : 'Off'}</span>
      </label>
      <small>{help}</small>
    </div>
  )
}

function FolderRow({
  label,
  advanced = false,
  value,
  isDefault,
  problem,
  error,
  onChange,
  onBrowse,
  onDefault,
}: {
  label: string
  /** An expert setting: the label stands out while advanced mode is on. */
  advanced?: boolean
  value: string
  isDefault: boolean
  problem: string | null
  error: string | undefined
  onChange: (value: string) => void
  onBrowse: () => void
  onDefault: () => void
}) {
  return (
    <div className={styles.field}>
      <span className={advanced ? advancedLabel : undefined}>{label}</span>
      <div className={styles.rootForm}>
        <input className={`${ui.input} ${ui.mono}`} value={value} onChange={(e) => onChange(e.target.value)} aria-label={label} spellCheck={false} />
        <button className={styles.secondary} type="button" onClick={onBrowse}>
          Browse…
        </button>
        {!isDefault && (
          <button className={styles.secondary} type="button" onClick={onDefault}>
            Default
          </button>
        )}
      </div>
      {(error ?? problem) && <span className={styles.error}>{error ?? problem}</span>}
    </div>
  )
}

export function DownloadsSection() {
  const queryClient = useQueryClient()
  const settings = useQuery({ queryKey: releaseKeys.downloads, queryFn: releasesApi.downloadSettings })
  const [draft, setDraft] = useState<DownloadDraft>({})
  const [picking, setPicking] = useState<FolderField | null>(null)
  const advanced = useAdvanced()

  const save = useMutation({
    mutationFn: (change: DownloadSettingsChange) => releasesApi.saveDownloadSettings(change),
    onSuccess: (data) => {
      queryClient.setQueryData(releaseKeys.downloads, data)
      setDraft({})
    },
    onError: (error) => {
      if (error instanceof ApiError && Object.keys(error.fieldErrors).some((name) => advancedDownloadFields.includes(name))) setAdvanced(true)
    },
  })

  if (!settings.data) return null
  const current = settings.data

  const client = draft.client ?? current.client
  const folder = (field: FolderField) => draft[field] ?? current[field]
  const speedText = draft.speedLimit ?? String(current.speedLimitKBps)
  const speed = Number(speedText.trim() === '' ? '0' : speedText)
  const speedValid = Number.isInteger(speed) && speed >= 0
  const minFreeText = draft.minFreeSpace ?? String(current.minFreeSpaceGb)
  const minFree = Number(minFreeText.trim() === '' ? '0' : minFreeText)
  const minFreeValid = Number.isFinite(minFree) && minFree >= 0
  const triesText = draft.articleTries ?? String(current.articleTries)
  const tries = Number(triesText)
  const triesValid = Number.isInteger(tries) && tries >= 1 && tries <= 10
  const keepFiles = draft.keepFiles ?? current.keepFiles
  const abortHopeless = draft.abortHopeless ?? current.abortHopeless
  const valid = speedValid && minFreeValid && triesValid

  const change: DownloadSettingsChange = {}
  if (client !== current.client) change.client = client
  for (const field of ['nzbFolder', 'incompleteFolder', 'completeFolder'] as const) {
    const value = draft[field]
    if (value !== undefined && value.trim() !== current[field]) change[field] = value.trim()
  }
  if (speedValid && speed !== current.speedLimitKBps) change.speedLimitKBps = speed
  if (minFreeValid && minFree !== current.minFreeSpaceGb) change.minFreeSpaceGb = minFree
  if (triesValid && tries !== current.articleTries) change.articleTries = tries
  if (keepFiles !== current.keepFiles) change.keepFiles = keepFiles
  if (abortHopeless !== current.abortHopeless) change.abortHopeless = abortHopeless
  const dirty = Object.keys(change).length > 0

  const fieldErrors = save.error instanceof ApiError ? save.error.fieldErrors : {}
  const generalError = save.error && Object.keys(fieldErrors).length === 0 ? save.error.message : undefined
  const fieldError = (field: string) => fieldErrors[field]?.[0]
  const showFolders =
    advanced ||
    current.incompleteProblem != null ||
    current.completeProblem != null ||
    fieldError('incompleteFolder') != null ||
    fieldError('completeFolder') != null

  const submit = (event: FormEvent) => {
    event.preventDefault()
    if (dirty && valid) save.mutate(change)
  }

  const setField = (field: keyof DownloadDraft, value: string | boolean) => {
    save.reset()
    setDraft((d) => ({ ...d, [field]: value }))
  }

  const isDefault = (field: FolderField) => {
    const value = folder(field)
    if (field === 'nzbFolder') return value === current.defaultNzbFolder || value === ''
    if (field === 'incompleteFolder') return value === current.defaultIncompleteFolder || value === ''
    return value === current.defaultCompleteFolder || value === ''
  }

  const row = (field: FolderField, label: string, problem: string | null, expert = false) => (
    <FolderRow
      label={label}
      advanced={expert}
      value={folder(field)}
      isDefault={isDefault(field)}
      problem={draft[field] === undefined ? problem : null}
      error={fieldError(field)}
      onChange={(value) => setField(field, value)}
      onBrowse={() => setPicking(field)}
      onDefault={() => setField(field, '')}
    />
  )

  return (
    <form className={page.panel} onSubmit={submit}>
      <h2 className={page.panelTitle}>Downloads</h2>
      <div className={styles.grid}>
        <label className={styles.field}>
          <span>Download client</span>
          <select value={client} onChange={(e) => setField('client', e.target.value)}>
            <option value="builtin">StreamZH (built-in Usenet downloader)</option>
            <option value="blackhole">Blackhole folder (SABnzbd, NZBGet …)</option>
          </select>
          {fieldError('client') && <span className={styles.error}>{fieldError('client')}</span>}
        </label>
        {client === 'builtin' && (
          <label className={styles.field}>
            <span>Speed limit (KB/s, 0 = unlimited)</span>
            <input type="number" min={0} step={1} inputMode="numeric" value={speedText} onChange={(e) => setField('speedLimit', e.target.value)} />
            {!speedValid && <span className={styles.error}>Enter a whole number, 0 or more.</span>}
            {speedValid && fieldError('speedLimitKBps') && <span className={styles.error}>{fieldError('speedLimitKBps')}</span>}
          </label>
        )}
      </div>

      {client === 'builtin' ? (
        <>
          <p className={styles.text}>
            StreamZH downloads grabbed releases from your Usenet servers, repairs and unpacks them. Files are assembled in the incomplete folder and
            moved to the completed folder (one subfolder per category) when they are done.
          </p>
          {showFolders && (
            <>
              {row('incompleteFolder', 'Incomplete downloads', current.incompleteProblem, true)}
              {row('completeFolder', 'Completed downloads', current.completeProblem, true)}
            </>
          )}
          <Advanced>
            <div className={styles.grid}>
              <label className={styles.field}>
                <span className={advancedLabel}>Minimum free space (GB)</span>
                <input
                  type="number"
                  min={0}
                  step="any"
                  inputMode="decimal"
                  value={minFreeText}
                  onChange={(e) => setField('minFreeSpace', e.target.value)}
                />
                <small>Downloads pause when less is free in the incomplete folder and continue with 50 % more. 0 never pauses.</small>
                {!minFreeValid && <span className={styles.error}>Enter a number, 0 or more.</span>}
                {minFreeValid && fieldError('minFreeSpaceGb') && <span className={styles.error}>{fieldError('minFreeSpaceGb')}</span>}
              </label>
              <label className={styles.field}>
                <span className={advancedLabel}>Tries per article</span>
                <input type="number" min={1} max={10} step={1} inputMode="numeric" value={triesText} onChange={(e) => setField('articleTries', e.target.value)} />
                <small>How often an article is asked for again on one server after a timeout or a dropped connection, before the next server.</small>
                {!triesValid && <span className={styles.error}>Enter a whole number from 1 to 10.</span>}
                {triesValid && fieldError('articleTries') && <span className={styles.error}>{fieldError('articleTries')}</span>}
              </label>
              <SwitchField
                label="Abort hopeless downloads"
                checked={abortHopeless}
                onChange={(checked) => setField('abortHopeless', checked)}
                help="A download fails as soon as more is missing than its par2 files can repair, so the next release can be tried. Off: it is downloaded to the end anyway."
              />
              <SwitchField
                label="Keep par2 and archive files"
                checked={keepFiles}
                onChange={(checked) => setField('keepFiles', checked)}
                help="After a successful unpack the download folder (par2, rar, nfo) stays in the incomplete folder instead of being deleted. You clean it up yourself."
              />
            </div>
          </Advanced>
          <AdvancedHint count={(showFolders ? 0 : 2) + 4} />
        </>
      ) : (
        <>
          <p className={styles.text}>
            Grabbed releases are saved as NZB files in this folder. Let SABnzbd or NZBGet watch it (SABnzbd: Config → Folders → Watched Folder) and they
            download them. Passwords are kept in the file name as <code>title{'{{password}}'}.nzb</code>.
          </p>
          {row('nzbFolder', 'NZB folder', current.problem)}
        </>
      )}

      <div className={styles.actions}>
        <button className={styles.primary} type="submit" disabled={!dirty || !valid || save.isPending}>
          {save.isPending ? 'Saving…' : 'Save'}
        </button>
        {dirty && (
          <button className={styles.secondary} type="button" onClick={() => setDraft({})} disabled={save.isPending}>
            Undo changes
          </button>
        )}
        {save.isSuccess && !dirty && <span className={styles.ok}>Saved</span>}
        {generalError && <span className={styles.error}>{generalError}</span>}
      </div>
      {picking && (
        <FolderPicker
          initialPath={folder(picking)}
          onClose={() => setPicking(null)}
          onSelect={(selected) => {
            setField(picking, selected)
            setPicking(null)
          }}
        />
      )}
    </form>
  )
}
