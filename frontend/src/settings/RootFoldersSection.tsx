import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { ApiError } from '../api/client'
import { libraryApi, queryKeys, type MediaType } from '../api/library'
import { outsideStorageCode, storageApi, storageKeys } from '../api/storage'
import FolderPicker from '../components/FolderPicker'
import Icon from '../components/Icon'
import { Skeleton } from '../components/Loader'
import ui from '../components/ui.module.css'
import { formatBytes } from '../lib/format'
import styles from '../pages/Settings.module.css'
import { Section } from './SettingsKit'
import { useIsOwner } from './useIsOwner'

export function RootFoldersSection({ windows = false }: { windows?: boolean }) {
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

  // 0.12: a folder outside the storage locations; the owner allows it in one click and the folder is added.
  const owner = useIsOwner()
  const outside = add.error instanceof ApiError && add.error.code === outsideStorageCode
  const allow = useMutation({
    mutationFn: () => storageApi.add(path.trim()),
    onSuccess: (data) => {
      queryClient.setQueryData(storageKeys.storage, data)
      add.mutate()
    },
  })

  const submit = (event: FormEvent) => {
    event.preventDefault()
    add.mutate()
  }

  return (
    <Section id="root-folders" title="Root folders" description="The folders where your movies and series live. StreamZH reads them and imports finished downloads into them.">
      {folders.isPending && <Skeleton rows={2} />}
      {(folders.data ?? []).length > 0 && (
        <ul className={styles.rootList}>
          {folders.data!.map((f) => (
            <li key={f.id}>
              <span className={styles.rootIcon} aria-hidden="true">
                <Icon name={f.mediaType === 'Movie' ? 'film' : 'tv'} size={16} />
              </span>
              <div className={styles.rootMain}>
                <code className={styles.rootPath}>{f.path}</code>
                <span className={styles.rootMeta}>
                  <span className={`${ui.pill} ${f.accessible ? ui.pillOk : ui.pillErr}`}>{f.mediaType === 'Movie' ? 'Movies' : 'Series'}</span>
                  {f.accessible && !f.writable && (
                    <span className={`${ui.pill} ${ui.pillWarn}`} title={f.writeProblem ?? undefined}>
                      Read only
                    </span>
                  )}
                  <span className={ui.num}>
                    {!f.accessible ? 'Not reachable' : f.freeSpace != null ? `${formatBytes(f.freeSpace)} free` : ''}
                  </span>
                </span>
              </div>
              <div className={styles.rootSide}>
                {f.unmappedFolders > 0 && (
                  <Link className={`${ui.button} ${ui.sm}`} to={`/library/import?type=${f.mediaType}`}>
                    <Icon name="folderplus" size={14} />
                    Import {f.unmappedFolders}
                  </Link>
                )}
                <button
                  className={`${ui.ghost} ${ui.sm} ${ui.icon}`}
                  type="button"
                  onClick={() => remove.mutate(f.id)}
                  disabled={remove.isPending}
                  aria-label={`Remove ${f.path}`}
                  title="Remove"
                >
                  <Icon name="trash" size={15} />
                </button>
              </div>
            </li>
          ))}
        </ul>
      )}
      {(folders.data ?? []).some((f) => f.accessible && !f.writable) && (
        <p className={styles.text}>
          <strong>Read only</strong> folders can be scanned, but StreamZH cannot import finished downloads into them.
          {(folders.data ?? []).find((f) => f.writeProblem)?.writeProblem && (
            <>
              {' '}
              Reason: {(folders.data ?? []).find((f) => f.writeProblem)?.writeProblem}
            </>
          )}
          {windows && (
            <>
              {' '}
              If Windows <strong>Controlled folder access</strong> is on (Windows Security → Ransomware protection), allow StreamZH.exe there.
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
        <button className={ui.button} type="button" onClick={() => setPicking(true)}>
          <Icon name="folder" size={15} />
          Browse…
        </button>
        <select className={ui.select} value={mediaType} onChange={(e) => setMediaType(e.target.value as MediaType)} aria-label="Media type">
          <option value="Movie">Movies</option>
          <option value="Series">Series</option>
        </select>
        <button className={ui.primary} type="submit" disabled={!path.trim() || add.isPending}>
          <Icon name="plus" size={15} />
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
      {add.error && !(outside && owner) && (
        <p className={styles.error}>{add.error instanceof ApiError ? add.error.message : 'Could not add the folder.'}</p>
      )}
      {outside && owner && (
        <div className={`${ui.infoBox} ${styles.allowBox}`} role="status">
          <span>
            <code>{path.trim()}</code> is outside the storage locations (the folders StreamZH may use, Settings → General → Storage).
          </span>
          <button className={`${ui.primary} ${ui.sm}`} type="button" onClick={() => allow.mutate()} disabled={allow.isPending || add.isPending}>
            <Icon name="check" size={14} />
            Allow and add
          </button>
          {allow.error && <span className={styles.error}>{allow.error instanceof ApiError ? (allow.error.fieldErrors.path?.[0] ?? allow.error.message) : allow.error.message}</span>}
        </div>
      )}
    </Section>
  )
}
