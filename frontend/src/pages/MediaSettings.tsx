import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { ApiError } from '../api/client'
import { libraryApi, queryKeys, type MediaType } from '../api/library'
import { releaseKeys, releasesApi } from '../api/releases'
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

export function DownloadsSection() {
  const queryClient = useQueryClient()
  const settings = useQuery({ queryKey: releaseKeys.downloads, queryFn: releasesApi.downloadSettings })
  const [path, setPath] = useState<string | null>(null)
  const [picking, setPicking] = useState(false)
  const value = path ?? settings.data?.nzbFolder ?? ''

  const save = useMutation({
    mutationFn: (folder: string) => releasesApi.saveDownloadSettings(folder),
    onSuccess: (data) => {
      queryClient.setQueryData(releaseKeys.downloads, data)
      setPath(null)
    },
  })

  const submit = (event: FormEvent) => {
    event.preventDefault()
    save.mutate(value.trim())
  }

  if (!settings.data) return null

  const error = save.error instanceof ApiError ? (save.error.fieldErrors.nzbFolder?.[0] ?? save.error.message) : save.error?.message
  const dirty = path !== null && path.trim() !== settings.data.nzbFolder

  return (
    <form className={page.panel} onSubmit={submit}>
      <h2 className={page.panelTitle}>Downloads</h2>
      <p className={styles.text}>
        Grabbed releases are saved as NZB files in this folder. Until StreamZH&apos;s own download engine arrives, let SABnzbd watch this folder
        (Config → Folders → Watched Folder) and it downloads them automatically.
      </p>
      <div className={styles.rootForm}>
        <input className={`${ui.input} ${ui.mono}`} value={value} onChange={(e) => setPath(e.target.value)} aria-label="NZB folder" spellCheck={false} />
        <button className={styles.secondary} type="button" onClick={() => setPicking(true)}>
          Browse…
        </button>
        <button className={styles.primary} type="submit" disabled={!dirty || save.isPending}>
          {save.isPending ? 'Saving…' : 'Save'}
        </button>
      </div>
      <div className={styles.actions}>
        {settings.data.isDefault ? (
          <span className={styles.muted}>Default folder inside the StreamZH data folder.</span>
        ) : (
          <button className={styles.secondary} type="button" onClick={() => save.mutate('')} disabled={save.isPending}>
            Use default folder
          </button>
        )}
        {save.isSuccess && !dirty && <span className={styles.ok}>Saved</span>}
        {error && <span className={styles.error}>{error}</span>}
        {!error && settings.data.problem && <span className={styles.error}>{settings.data.problem}</span>}
      </div>
      {picking && (
        <FolderPicker
          initialPath={value}
          onClose={() => setPicking(false)}
          onSelect={(selected) => {
            setPath(selected)
            setPicking(false)
          }}
        />
      )}
    </form>
  )
}
