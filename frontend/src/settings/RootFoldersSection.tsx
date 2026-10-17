import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { ApiError } from '../api/client'
import { libraryApi, queryKeys, type MediaType } from '../api/library'
import FolderPicker from '../components/FolderPicker'
import ui from '../components/ui.module.css'
import { formatBytes } from '../lib/format'
import page from '../pages/Page.module.css'
import styles from '../pages/Settings.module.css'

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
