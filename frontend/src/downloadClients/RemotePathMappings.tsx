import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useId, useState, type FormEvent } from 'react'
import { ApiError } from '../api/client'
import { clientAddress, downloadClientKeys, downloadClientsApi, type DownloadClient, type RemotePathMapping } from '../api/downloadClients'
import FolderPicker from '../components/FolderPicker'
import Icon from '../components/Icon'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import { Section } from '../settings/SettingsKit'
import styles from './DownloadClients.module.css'

const empty: RemotePathMapping = { id: 0, host: '', remotePath: '', localPath: '' }

/**
 * Remote path mappings (Radarr's): when a client runs on another machine or in another container, the folder it
 * reports is translated to the folder StreamZH sees before importing. An expert section until the first mapping exists.
 */
export function RemotePathMappings({ clients, canEdit }: { clients: DownloadClient[]; canEdit: boolean }) {
  const queryClient = useQueryClient()
  const mappings = useQuery({ queryKey: downloadClientKeys.mappings, queryFn: downloadClientsApi.mappings })
  const [editing, setEditing] = useState<RemotePathMapping | null>(null)
  const [deleting, setDeleting] = useState<number | null>(null)
  const remove = useMutation({
    mutationFn: (id: number) => downloadClientsApi.removeMapping(id),
    onSuccess: async () => {
      setDeleting(null)
      await queryClient.invalidateQueries({ queryKey: downloadClientKeys.mappings })
    },
  })

  const list = mappings.data ?? []
  const hosts = Array.from(new Set(clients.map((c) => (typeof c.settings.host === 'string' ? c.settings.host : '')).filter(Boolean)))

  return (
    <Section
      id="remote-path-mappings"
      title="Remote path mappings"
      advanced={list.length === 0 && !mappings.isError}
      description="Only needed when a torrent client runs on another machine or in another container and sees the download folder under a different path."
      actions={
        canEdit ? (
          <button type="button" className={`${ui.button} ${ui.sm}`} onClick={() => setEditing(empty)}>
            <Icon name="plus" size={14} />
            Add mapping
          </button>
        ) : undefined
      }
    >
      {mappings.isError && (
        <p className={ui.error}>
          {mappings.error instanceof ApiError && mappings.error.status === 404 ? 'This build has no remote path mappings yet.' : mappings.error.message}
        </p>
      )}
      {mappings.isSuccess && list.length === 0 && <p className={styles.sectionNote}>No mappings. StreamZH uses the paths the clients report as they are.</p>}
      {list.length > 0 && (
        <div className={styles.tableWrap}>
          <table className={`${ui.table} ${styles.mappingTable}`}>
            <thead>
              <tr>
                <th>Host</th>
                <th>Remote path</th>
                <th aria-hidden="true" />
                <th>Local path</th>
                {canEdit && (
                  <th>
                    <span className={styles.srOnly}>Actions</span>
                  </th>
                )}
              </tr>
            </thead>
            <tbody>
              {list.map((m) => (
                <tr key={m.id}>
                  <td data-label="Host">{m.host}</td>
                  <td data-label="Remote path" className={ui.mono}>
                    {m.remotePath}
                  </td>
                  <td className={styles.arrow} aria-hidden="true">
                    <Icon name="right" size={14} />
                  </td>
                  <td data-label="Local path" className={ui.mono}>
                    {m.localPath}
                  </td>
                  {canEdit && (
                    <td className={styles.rowActions}>
                      {deleting === m.id ? (
                        <span className={styles.inlineConfirm}>
                          <button type="button" className={`${ui.dangerSolid} ${ui.sm}`} onClick={() => remove.mutate(m.id)} disabled={remove.isPending}>
                            Delete
                          </button>
                          <button type="button" className={`${ui.ghost} ${ui.sm}`} onClick={() => setDeleting(null)}>
                            Keep
                          </button>
                        </span>
                      ) : (
                        <>
                          <button type="button" className={`${ui.ghost} ${ui.icon} ${ui.sm}`} onClick={() => setEditing(m)} aria-label={`Edit mapping for ${m.host}`} title="Edit">
                            <Icon name="edit" size={15} />
                          </button>
                          <button
                            type="button"
                            className={`${ui.ghost} ${ui.icon} ${ui.sm} ${styles.dangerIcon}`}
                            onClick={() => setDeleting(m.id)}
                            aria-label={`Delete mapping for ${m.host}`}
                            title="Delete"
                          >
                            <Icon name="trash" size={15} />
                          </button>
                        </>
                      )}
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {remove.error && <p className={ui.error}>{remove.error.message}</p>}
      {editing && <MappingDialog mapping={editing} hosts={hosts} clients={clients} onClose={() => setEditing(null)} />}
    </Section>
  )
}

function MappingDialog({ mapping, hosts, clients, onClose }: { mapping: RemotePathMapping; hosts: string[]; clients: DownloadClient[]; onClose: () => void }) {
  const queryClient = useQueryClient()
  const id = useId()
  const [draft, setDraft] = useState<RemotePathMapping>(mapping.id === 0 && hosts.length === 1 ? { ...mapping, host: hosts[0] } : mapping)
  const [picking, setPicking] = useState(false)
  const save = useMutation({
    mutationFn: () => downloadClientsApi.saveMapping({ ...draft, host: draft.host.trim(), remotePath: draft.remotePath.trim(), localPath: draft.localPath.trim() }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: downloadClientKeys.mappings })
      onClose()
    },
  })
  const [edited, setEdited] = useState<Set<string>>(new Set())
  const errors = save.error instanceof ApiError ? save.error.fieldErrors : {}
  const error = (name: keyof RemotePathMapping) => (edited.has(name) ? undefined : errors[name]?.[0])
  const change = (patch: Partial<RemotePathMapping>) => {
    setDraft((d) => ({ ...d, ...patch }))
    setEdited((e) => new Set([...e, ...Object.keys(patch)]))
  }
  const submit = (e: FormEvent) => {
    e.preventDefault()
    setEdited(new Set())
    save.mutate()
  }
  const usedBy = clients.filter((c) => c.settings.host === draft.host).map((c) => `${c.name} (${clientAddress(c)})`)

  return (
    <Modal title={mapping.id === 0 ? 'Add remote path mapping' : 'Edit remote path mapping'} onClose={onClose}>
      <form className={styles.mappingForm} onSubmit={submit} noValidate>
        <label className={ui.field}>
          Host
          <small>The host of the download client, exactly as in its settings.</small>
          <input
            className={`${ui.input} ${error('host') ? styles.inputError : ''}`}
            list={`${id}-hosts`}
            value={draft.host}
            placeholder="client.example.lan"
            autoComplete="off"
            spellCheck={false}
            onChange={(e) => change({ host: e.target.value })}
            autoFocus
          />
          <datalist id={`${id}-hosts`}>
            {hosts.map((h) => (
              <option key={h} value={h} />
            ))}
          </datalist>
          {usedBy.length > 0 && <small className={styles.okText}>Used by {usedBy.join(', ')}</small>}
          {error('host') && <small className={styles.fieldError}>{error('host')}</small>}
        </label>
        <label className={ui.field}>
          Remote path
          <small>The folder as the client reports it, e.g. /downloads/complete/.</small>
          <input
            className={`${ui.input} ${ui.mono} ${error('remotePath') ? styles.inputError : ''}`}
            value={draft.remotePath}
            placeholder="/downloads/"
            spellCheck={false}
            onChange={(e) => change({ remotePath: e.target.value })}
          />
          {error('remotePath') && <small className={styles.fieldError}>{error('remotePath')}</small>}
        </label>
        <div className={ui.field}>
          <label htmlFor={`${id}-local`}>Local path</label>
          <small>The same folder as StreamZH sees it.</small>
          <div className={styles.pathRow}>
            <input
              id={`${id}-local`}
              className={`${ui.input} ${ui.mono} ${error('localPath') ? styles.inputError : ''}`}
              value={draft.localPath}
              placeholder="/data/torrents/"
              spellCheck={false}
              onChange={(e) => change({ localPath: e.target.value })}
            />
            <button className={ui.button} type="button" onClick={() => setPicking(true)}>
              Browse…
            </button>
          </div>
          {error('localPath') && <small className={styles.fieldError}>{error('localPath')}</small>}
        </div>
        {save.error && Object.keys(errors).length === 0 && (
          <p className={ui.error} role="alert">
            {save.error.message}
          </p>
        )}
        <div className={styles.dialogActions}>
          <button type="button" className={ui.button} onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className={ui.primary} disabled={save.isPending}>
            {save.isPending ? 'Saving…' : 'Save'}
          </button>
        </div>
      </form>
      {picking && (
        <FolderPicker
          initialPath={draft.localPath}
          onClose={() => setPicking(false)}
          onSelect={(localPath) => {
            change({ localPath })
            setPicking(false)
          }}
        />
      )}
    </Modal>
  )
}
