import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { ApiError } from '../api/client'
import { storageApi, storageKeys } from '../api/storage'
import FolderPicker from '../components/FolderPicker'
import Icon from '../components/Icon'
import { Skeleton } from '../components/Loader'
import ui from '../components/ui.module.css'
import list from '../pages/Settings.module.css'
import styles from './General.module.css'
import { OwnerOnlyNote } from './OwnerOnly'
import { Row, Section } from './SettingsKit'

/**
 * Settings → General → Storage: where StreamZH keeps its own data, and the storage locations (0.12): the folders
 * StreamZH may read and write media and downloads in. Root, download and other folders must be inside one of them.
 */
export function StorageSection({ dataDirectory, logDirectory }: { dataDirectory: string; logDirectory: string }) {
  const queryClient = useQueryClient()
  const storage = useQuery({ queryKey: storageKeys.storage, queryFn: storageApi.get })
  const [path, setPath] = useState('')
  const [picking, setPicking] = useState(false)
  const owner = storage.data?.canChange ?? false

  const add = useMutation({
    mutationFn: () => storageApi.add(path.trim()),
    onSuccess: (data) => {
      queryClient.setQueryData(storageKeys.storage, data)
      setPath('')
    },
  })
  const remove = useMutation({
    mutationFn: storageApi.remove,
    onSuccess: (data) => queryClient.setQueryData(storageKeys.storage, data),
  })

  const submit = (event: FormEvent) => {
    event.preventDefault()
    if (path.trim()) add.mutate()
  }

  return (
    <Section
      id="storage"
      title="Storage"
      description="Storage locations are the folders StreamZH may use for media and downloads. Root folders, download folders and everything else you pick must be inside one of them."
      footer={owner ? undefined : <OwnerOnlyNote>Only the owner can add or remove storage locations.</OwnerOnlyNote>}
    >
      {storage.isPending && <Skeleton rows={2} />}
      {storage.isError && <p className={ui.error}>{storage.error.message}</p>}
      {storage.data && storage.data.locations.length === 0 && (
        <p className={styles.warn}>No storage locations: nothing can be downloaded or imported until the owner adds one.</p>
      )}
      {storage.data && storage.data.locations.length > 0 && (
        <ul className={list.rootList} aria-label="Storage locations">
          {storage.data.locations.map((l) => (
            <li key={l.path}>
              <span className={list.rootIcon} aria-hidden="true">
                <Icon name={l.path.startsWith('\\\\') || l.path.startsWith('//') ? 'network' : 'hdd'} size={16} />
              </span>
              <div className={list.rootMain}>
                <code className={list.rootPath}>{l.path}</code>
                <span className={list.rootMeta}>
                  {!l.exists && <span className={`${ui.pill} ${ui.pillErr}`}>Not reachable</span>}
                  {l.path === storage.data.defaultDownloadFolder && <span className={ui.pill}>Default download folder</span>}
                  <span>{l.uses.length === 0 ? 'Not used yet' : l.uses.join(' · ')}</span>
                </span>
              </div>
              {owner && (
                <div className={list.rootSide}>
                  <button
                    className={`${ui.ghost} ${ui.sm} ${ui.icon}`}
                    type="button"
                    onClick={() => remove.mutate(l.path)}
                    disabled={remove.isPending}
                    aria-label={`Remove ${l.path}`}
                    title="Remove"
                  >
                    <Icon name="trash" size={15} />
                  </button>
                </div>
              )}
            </li>
          ))}
        </ul>
      )}
      {remove.error && <p className={ui.error}>{errorText(remove.error)}</p>}

      {owner && (
        <form className={list.rootForm} onSubmit={submit}>
          <input
            className={`${ui.input} ${ui.mono}`}
            placeholder="Click to browse, or type a full path"
            value={path}
            onChange={(e) => setPath(e.target.value)}
            onClick={() => !path && setPicking(true)}
            aria-label="Storage location"
            spellCheck={false}
          />
          <button className={ui.button} type="button" onClick={() => setPicking(true)}>
            <Icon name="folder" size={15} />
            Browse…
          </button>
          <button className={ui.primary} type="submit" disabled={!path.trim() || add.isPending}>
            <Icon name="plus" size={15} />
            Add location
          </button>
        </form>
      )}
      {add.error && <p className={ui.error}>{errorText(add.error)}</p>}

      <Row label="Data folder" wide help="Database, settings, logs and keys. Set with --data or STREAMZH_DATA.">
        <code className={styles.path}>{dataDirectory}</code>
      </Row>
      <Row label="Log folder" wide>
        <code className={styles.path}>{logDirectory}</code>
      </Row>

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
    </Section>
  )
}

function errorText(error: Error) {
  return error instanceof ApiError ? (error.fieldErrors.path?.[0] ?? error.message) : error.message
}
