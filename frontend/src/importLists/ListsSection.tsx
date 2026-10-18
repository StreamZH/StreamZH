import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { importListApi, importListKeys, newImportList, type ImportList, type ImportListSchema, type ImportListType } from '../api/importLists'
import { libraryApi, queryKeys } from '../api/library'
import { useAuth } from '../auth/auth'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import connect from '../connect/Connect.module.css'
import { Pill } from '../indexers/Chips'
import { Section } from '../settings/SettingsKit'
import ListForm from './ListForm'
import TypePicker from './TypePicker'
import { formatTime, mediaLabel } from './labels'
import styles from './ImportLists.module.css'

type View = { kind: 'none' } | { kind: 'pick' } | { kind: 'form'; list: ImportList; type: ImportListType }

/** The lists as cards (like Connect and Radarr's Lists page), with add, edit and "Sync all". */
export function ListsSection() {
  const { user } = useAuth()
  const canEdit = user?.role === 'Owner' || user?.role === 'Admin'
  const queryClient = useQueryClient()
  const lists = useQuery({ queryKey: importListKeys.list, queryFn: importListApi.list, refetchInterval: 15_000 })
  const schema = useQuery({ queryKey: importListKeys.schema, queryFn: importListApi.schema, staleTime: 10 * 60_000 })
  const roots = useQuery({ queryKey: queryKeys.rootFolders, queryFn: libraryApi.rootFolders })
  const [view, setView] = useState<View>({ kind: 'none' })
  const close = () => setView({ kind: 'none' })
  const syncAll = useMutation({
    mutationFn: importListApi.syncAll,
    onSuccess: () => setTimeout(() => void queryClient.invalidateQueries({ queryKey: importListKeys.list }), 3000),
  })

  const types = schema.data?.types ?? []
  const typeOf = (key: string) => types.find((t) => t.key === key)
  const all = lists.data ?? []
  const enabled = all.filter((l) => l.enabled).length

  const start = (type: ImportListType) => {
    const root = (roots.data ?? []).find((r) => r.mediaType === type.mediaTypes[0])
    setView({ kind: 'form', list: newImportList(type, root?.id ?? 0, uniqueName(type.name, all)), type })
  }

  return (
    <Section
      id="lists"
      title="Lists"
      description="StreamZH reads every enabled list on a schedule and adds what is new, with the root folder, profile and monitoring you choose per list."
    >
      {lists.isError && <p className={ui.error}>{lists.error.message}</p>}
      {canEdit && all.length > 0 && (
        <div className={styles.sectionActions}>
          <button className={`${ui.button} ${ui.sm}`} type="button" onClick={() => syncAll.mutate()} disabled={syncAll.isPending || enabled === 0}>
            <Icon name="restart" size={14} />
            {syncAll.isPending ? 'Starting…' : 'Sync all lists'}
          </button>
          {syncAll.isSuccess && (
            <span className={styles.note}>{syncAll.data.queued ? 'Sync started. The cards show the result.' : 'A sync is already waiting.'}</span>
          )}
          {syncAll.error && <span className={ui.error}>{syncAll.error.message}</span>}
        </div>
      )}
      <ul className={connect.grid}>
        {all.map((l) => (
          <ListCard key={l.id} list={l} canEdit={canEdit && !!typeOf(l.implementation) && !!schema.data} onEdit={() => {
            const type = typeOf(l.implementation)
            if (type) setView({ kind: 'form', list: l, type })
          }} />
        ))}
        {canEdit && (
          <li>
            <button className={connect.addCard} type="button" onClick={() => setView({ kind: 'pick' })} disabled={!schema.isSuccess}>
              <span className={connect.plus} aria-hidden="true">
                <Icon name="plus" size={16} />
              </span>
              Add list
            </button>
          </li>
        )}
      </ul>
      {lists.isSuccess && all.length === 0 && !canEdit && <p className={ui.muted}>None yet. An admin can add them.</p>}

      {view.kind === 'pick' && <TypePicker types={types} onClose={close} onSelect={start} />}
      {view.kind === 'form' && schema.data && (
        <ListForm
          key={`${view.list.id}-${view.type.key}`}
          list={view.list}
          type={view.type}
          schema={schema.data as ImportListSchema}
          rootFolders={roots.data ?? []}
          canEdit={canEdit}
          onClose={close}
          onBack={view.list.id === 0 ? () => setView({ kind: 'pick' }) : undefined}
        />
      )}
    </Section>
  )
}

/** "Plex watchlist", then "Plex watchlist 2" … */
function uniqueName(name: string, existing: ImportList[]) {
  const taken = new Set(existing.map((l) => l.name.toLowerCase()))
  let result = name
  for (let n = 2; taken.has(result.toLowerCase()); n++) result = `${name} ${n}`
  return result
}

function ListCard({ list: l, canEdit, onEdit }: { list: ImportList; canEdit: boolean; onEdit: () => void }) {
  const failing = l.enabled && l.status.failing
  const state = !l.enabled ? connect.disabled : failing ? connect.failing : ''
  const s = l.status
  return (
    <li>
      <button type="button" className={`${connect.card} ${state}`} onClick={onEdit} disabled={!canEdit}>
        <span className={connect.cardHead}>
          <span className={connect.cardIcon} aria-hidden="true">
            <Icon name={l.mediaType === 'Series' ? 'tv' : l.mediaType === 'Movie' ? 'film' : 'list'} size={16} />
          </span>
          <span className={connect.cardTitle}>
            <span className={connect.cardName}>{l.name}</span>
            <span className={connect.cardType}>
              {l.implementationName} · {mediaLabel(l.mediaType)}
            </span>
          </span>
          {!l.enabled ? <Pill tone="off">Disabled</Pill> : failing ? <Pill tone="error">Failing</Pill> : !l.enableAuto ? <Pill tone="info">Manual</Pill> : null}
        </span>
        {failing ? (
          <span className={connect.cardError}>
            {s.lastError}
            {s.lastSync && <span className={connect.cardTime}> · {formatTime(s.lastSync)}</span>}
          </span>
        ) : (
          <span className={styles.cardSync}>
            {s.lastSuccess ? (
              <>
                Synced {formatTime(s.lastSuccess)} · {s.lastFound ?? 0} found · {s.lastAdded ?? 0} added
                {s.lastAddedTitles.length > 0 && <span className={styles.cardAdded}>Last added: {s.lastAddedTitles.join(', ')}</span>}
              </>
            ) : (
              'Not synced yet'
            )}
          </span>
        )}
      </button>
    </li>
  )
}
