import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { ApiError } from '../api/client'
import { libraryApi, queryKeys, type MediaType } from '../api/library'
import { releaseKeys, releasesApi, type DownloadClient, type DownloadSettingsChange } from '../api/releases'
import FolderPicker from '../components/FolderPicker'
import ui from '../components/ui.module.css'
import { formatBytes } from '../lib/format'
import page from './Page.module.css'
import styles from './Settings.module.css'

export function MetadataSection() {
  const queryClient = useQueryClient()
  const settings = useQuery({ queryKey: queryKeys.metadata, queryFn: libraryApi.metadataSettings })
  const [token, setToken] = useState('')
  const [language, setLanguage] = useState<string | null>(null)

  const effectiveLanguage = language ?? settings.data?.language ?? 'en-US'

  const test = useMutation({ mutationFn: () => libraryApi.testMetadata(token.trim() || null) })
  const refreshAll = useMutation({ mutationFn: libraryApi.refreshAll })
  const save = useMutation({
    mutationFn: () => libraryApi.saveMetadataSettings(token.trim() || null, effectiveLanguage),
    onSuccess: async (data) => {
      queryClient.setQueryData(queryKeys.metadata, data)
      setToken('')
      test.reset()
    },
  })

  const submit = (event: FormEvent) => {
    event.preventDefault()
    save.mutate()
  }

  if (!settings.data) return null

  return (
    <form className={page.panel} onSubmit={submit}>
      <h2 className={page.panelTitle}>Metadata (TMDB)</h2>
      <p className={styles.text}>
        StreamZH finds titles, posters and episode lists on The Movie Database. Create a free account on{' '}
        <a href="https://www.themoviedb.org/settings/api" target="_blank" rel="noreferrer">
          themoviedb.org
        </a>{' '}
        and paste the <em>API Read Access Token</em> (or the shorter API key) here.
      </p>
      <div className={styles.grid}>
        <label className={styles.field}>
          <span>
            API token{' '}
            {settings.data.tmdbConfigured ? (
              <span className={`${ui.badge} ${ui.ok}`}>Saved {settings.data.tmdbTokenHint}</span>
            ) : (
              <span className={`${ui.badge} ${ui.missing}`}>Not set</span>
            )}
          </span>
          <input
            type="password"
            autoComplete="off"
            placeholder={settings.data.tmdbConfigured ? 'Leave empty to keep the saved token' : 'eyJhbGciOi…'}
            value={token}
            onChange={(e) => setToken(e.target.value)}
          />
        </label>
        <label className={styles.field}>
          <span>Language for titles and descriptions</span>
          <select value={effectiveLanguage} onChange={(e) => setLanguage(e.target.value)}>
            {settings.data.languages.map((l) => (
              <option key={l} value={l}>
                {l}
              </option>
            ))}
          </select>
        </label>
      </div>
      <div className={styles.actions}>
        <button className={styles.primary} type="submit" disabled={save.isPending || (!token.trim() && effectiveLanguage === settings.data.language)}>
          {save.isPending ? 'Saving…' : 'Save'}
        </button>
        <button
          className={styles.secondary}
          type="button"
          onClick={() => test.mutate()}
          disabled={test.isPending || (!token.trim() && !settings.data.tmdbConfigured)}
        >
          {test.isPending ? 'Testing…' : 'Test connection'}
        </button>
        {test.isSuccess && <span className={styles.ok}>TMDB connection works</span>}
        {test.error && <span className={styles.error}>{test.error.message}</span>}
        {save.isSuccess && !test.isSuccess && <span className={styles.ok}>Saved</span>}
        {save.error && <span className={styles.error}>{save.error.message}</span>}
      </div>
      <div className={styles.actions}>
        <button className={styles.secondary} type="button" onClick={() => refreshAll.mutate()} disabled={refreshAll.isPending || !settings.data.tmdbConfigured}>
          Refresh all metadata
        </button>
        <span className={styles.muted}>
          {refreshAll.isSuccess
            ? `${refreshAll.data.queued} titles queued, progress shows in the sidebar.`
            : 'Loads titles, posters and episodes again for the whole library. Happens automatically when the language changes.'}
        </span>
      </div>
      <p className={styles.attribution}>
        This product uses the TMDB API but is not endorsed or certified by TMDB. Swiss and Austrian variants use the German titles, since TMDB has few
        regional translations.
      </p>
    </form>
  )
}

export function RootFoldersSection() {
  const queryClient = useQueryClient()
  const folders = useQuery({ queryKey: queryKeys.rootFolders, queryFn: libraryApi.rootFolders })
  const [path, setPath] = useState('')
  const [mediaType, setMediaType] = useState<MediaType>('Movie')
  const [picking, setPicking] = useState(false)

  const add = useMutation({
    mutationFn: () => libraryApi.addRootFolder(path.trim(), mediaType),
    onSuccess: async () => {
      setPath('')
      await queryClient.invalidateQueries({ queryKey: queryKeys.rootFolders })
    },
  })

  const remove = useMutation({
    mutationFn: libraryApi.removeRootFolder,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.rootFolders }),
  })

  const submit = (event: FormEvent) => {
    event.preventDefault()
    add.mutate()
  }

  return (
    <section className={page.panel}>
      <h2 className={page.panelTitle}>Root folders</h2>
      <p className={styles.text}>The folders where your movies and series live. StreamZH reads them and, later, moves new downloads there.</p>

      {(folders.data ?? []).length > 0 && (
        <ul className={styles.rootList}>
          {folders.data!.map((f) => (
            <li key={f.id}>
              <span className={`${ui.badge} ${f.accessible ? ui.ok : ui.missing}`}>{f.mediaType === 'Movie' ? 'Movies' : 'Series'}</span>
              <code className={styles.rootPath}>{f.path}</code>
              {f.accessible && !f.writable && (
                <span className={`${ui.badge} ${ui.waiting}`} title={f.writeProblem ?? undefined}>
                  Read only
                </span>
              )}
              <span className={styles.rootMeta}>
                {!f.accessible
                  ? 'Not reachable'
                  : f.freeSpace != null
                    ? `${formatBytes(f.freeSpace)} free`
                    : ''}
              </span>
              {f.unmappedFolders > 0 && (
                <Link className={styles.rootImport} to={`/library/import?type=${f.mediaType}`}>
                  Import {f.unmappedFolders}
                </Link>
              )}
              <button className={styles.secondary} type="button" onClick={() => remove.mutate(f.id)} disabled={remove.isPending}>
                Remove
              </button>
            </li>
          ))}
        </ul>
      )}
      {(folders.data ?? []).some((f) => f.accessible && !f.writable) && (
        <p className={styles.text}>
          <strong>Read only</strong> folders can be scanned and imported, but StreamZH cannot move new downloads into them yet.
          {(folders.data ?? []).find((f) => f.writeProblem)?.writeProblem && (
            <>
              {' '}
              Reason: {(folders.data ?? []).find((f) => f.writeProblem)?.writeProblem}
            </>
          )}
        </p>
      )}
      {remove.error && <p className={styles.error}>{remove.error.message}</p>}

      <form className={styles.rootForm} onSubmit={submit}>
        <input
          className={ui.input}
          placeholder="Click to browse, or type a full path"
          value={path}
          onChange={(e) => setPath(e.target.value)}
          onClick={() => !path && setPicking(true)}
          aria-label="Folder path"
        />
        <button className={styles.secondary} type="button" onClick={() => setPicking(true)}>
          Browse…
        </button>
        <select className={ui.select} value={mediaType} onChange={(e) => setMediaType(e.target.value as MediaType)} aria-label="Media type">
          <option value="Movie">Movies</option>
          <option value="Series">Series</option>
        </select>
        <button className={styles.primary} type="submit" disabled={!path.trim() || add.isPending}>
          Add folder
        </button>
      </form>
      {picking && (
        <FolderPicker
          initialPath={path}
          onClose={() => setPicking(false)}
          onSelect={(selected) => {
            setPath(selected)
            setPicking(false)
          }}
        />
      )}
      {add.error && (
        <p className={styles.error}>{add.error instanceof ApiError ? add.error.message : 'Could not add the folder.'}</p>
      )}
    </section>
  )
}

type FolderField = 'nzbFolder' | 'incompleteFolder' | 'completeFolder'

interface DownloadDraft {
  client?: DownloadClient
  nzbFolder?: string
  incompleteFolder?: string
  completeFolder?: string
  speedLimit?: string
}

function FolderRow({
  label,
  value,
  isDefault,
  problem,
  error,
  onChange,
  onBrowse,
  onDefault,
}: {
  label: string
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
      <span>{label}</span>
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

  const save = useMutation({
    mutationFn: (change: DownloadSettingsChange) => releasesApi.saveDownloadSettings(change),
    onSuccess: (data) => {
      queryClient.setQueryData(releaseKeys.downloads, data)
      setDraft({})
    },
  })

  if (!settings.data) return null
  const current = settings.data

  const client = draft.client ?? current.client
  const folder = (field: FolderField) => draft[field] ?? current[field]
  const speedText = draft.speedLimit ?? String(current.speedLimitKBps)
  const speed = Number(speedText.trim() === '' ? '0' : speedText)
  const speedValid = Number.isInteger(speed) && speed >= 0

  const change: DownloadSettingsChange = {}
  if (client !== current.client) change.client = client
  for (const field of ['nzbFolder', 'incompleteFolder', 'completeFolder'] as const) {
    const value = draft[field]
    if (value !== undefined && value.trim() !== current[field]) change[field] = value.trim()
  }
  if (speedValid && speed !== current.speedLimitKBps) change.speedLimitKBps = speed
  const dirty = Object.keys(change).length > 0

  const fieldErrors = save.error instanceof ApiError ? save.error.fieldErrors : {}
  const generalError = save.error && Object.keys(fieldErrors).length === 0 ? save.error.message : undefined
  const fieldError = (field: string) => fieldErrors[field]?.[0]

  const submit = (event: FormEvent) => {
    event.preventDefault()
    if (dirty && speedValid) save.mutate(change)
  }

  const setField = (field: keyof DownloadDraft, value: string) => {
    save.reset()
    setDraft((d) => ({ ...d, [field]: value }))
  }

  const isDefault = (field: FolderField) => {
    const value = folder(field)
    if (field === 'nzbFolder') return value === current.defaultNzbFolder || value === ''
    if (field === 'incompleteFolder') return value === current.defaultIncompleteFolder || value === ''
    return value === current.defaultCompleteFolder || value === ''
  }

  const row = (field: FolderField, label: string, problem: string | null) => (
    <FolderRow
      label={label}
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
          {row('incompleteFolder', 'Incomplete downloads', current.incompleteProblem)}
          {row('completeFolder', 'Completed downloads', current.completeProblem)}
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
        <button className={styles.primary} type="submit" disabled={!dirty || !speedValid || save.isPending}>
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
