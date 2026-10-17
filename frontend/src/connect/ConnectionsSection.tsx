import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { connectApi, connectKeys, newConnection, triggerKeys, type Connection, type ConnectKind, type ConnectType } from '../api/connect'
import { useAuth } from '../auth/auth'
import ui from '../components/ui.module.css'
import { Pill } from '../indexers/Chips'
import { Section } from '../settings/SettingsKit'
import ConnectionForm from './ConnectionForm'
import TypePicker from './TypePicker'
import { kindHelp, kindLabels, triggerLabels } from './labels'
import styles from './Connect.module.css'

type View = { kind: 'none' } | { kind: 'pick'; filter: ConnectKind } | { kind: 'form'; connection: Connection; type: ConnectType }

function formatTime(iso: string | null) {
  if (!iso) return null
  const date = new Date(iso)
  const today = new Date().toDateString() === date.toDateString()
  return today
    ? date.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
    : date.toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })
}

/** Settings → Connect: media servers and notifications as cards (like Radarr's Connect page), with add, edit and test. */
export function ConnectionsSection() {
  const { user } = useAuth()
  const canEdit = user?.role === 'Owner' || user?.role === 'Admin'
  const connections = useQuery({ queryKey: connectKeys.list, queryFn: connectApi.list, refetchInterval: 15_000 })
  const schema = useQuery({ queryKey: connectKeys.schema, queryFn: connectApi.schema, staleTime: 10 * 60_000 })
  const [view, setView] = useState<View>({ kind: 'none' })
  const close = () => setView({ kind: 'none' })

  const types = schema.data ?? []
  const typeOf = (key: string) => types.find((t) => t.key === key)
  const edit = (c: Connection) => {
    const type = typeOf(c.implementation)
    if (type) setView({ kind: 'form', connection: c, type })
  }

  const kinds: ConnectKind[] = ['mediaServer', 'notification']

  return (
    <>
      {connections.isError && <p className={ui.error}>{connections.error.message}</p>}
      {kinds.map((kind) => {
        const list = (connections.data ?? []).filter((c) => c.kind === kind)
        return (
          <Section key={kind} id={kind === 'mediaServer' ? 'media-servers' : 'notifications'} title={kindLabels[kind]} description={kindHelp[kind]}>
            <ul className={styles.grid}>
              {list.map((c) => (
                <ConnectionCard key={c.id} connection={c} type={typeOf(c.implementation)} canEdit={canEdit} onEdit={() => edit(c)} />
              ))}
              {canEdit && (
                <li>
                  <button className={styles.addCard} type="button" onClick={() => setView({ kind: 'pick', filter: kind })} disabled={!schema.isSuccess}>
                    <span className={styles.plus} aria-hidden="true">
                      +
                    </span>
                    {kind === 'mediaServer' ? 'Add media server' : 'Add notification'}
                  </button>
                </li>
              )}
            </ul>
            {connections.isSuccess && list.length === 0 && !canEdit && <p className={ui.muted}>None yet. An admin can add them.</p>}
          </Section>
        )
      })}

      {view.kind === 'pick' && (
        <TypePicker
          types={types}
          kind={view.filter}
          onClose={close}
          onSelect={(type) => setView({ kind: 'form', connection: uniqueName(newConnection(type), connections.data ?? []), type })}
        />
      )}
      {view.kind === 'form' && (
        <ConnectionForm
          key={`${view.connection.id}-${view.type.key}`}
          connection={view.connection}
          type={view.type}
          canEdit={canEdit}
          onClose={close}
          onBack={view.connection.id === 0 ? () => setView({ kind: 'pick', filter: view.type.kind }) : undefined}
        />
      )}
    </>
  )
}

/** "Discord", then "Discord 2" … so a second connection of a type does not fail on the name. */
function uniqueName(connection: Connection, existing: Connection[]): Connection {
  const taken = new Set(existing.map((c) => c.name.toLowerCase()))
  let name = connection.name
  for (let n = 2; taken.has(name.toLowerCase()); n++) name = `${connection.name} ${n}`
  return { ...connection, name }
}

function ConnectionCard({
  connection: c,
  type,
  canEdit,
  onEdit,
}: {
  connection: Connection
  type: ConnectType | undefined
  canEdit: boolean
  onEdit: () => void
}) {
  const triggers = (type?.triggers ?? []).filter((t) => c[triggerKeys[t]])
  const failing = c.enable && c.status.failing
  const state = !c.enable ? styles.disabled : failing ? styles.failing : ''
  return (
    <li>
      <button type="button" className={`${styles.card} ${state}`} onClick={onEdit} disabled={!canEdit || !type}>
        <span className={styles.cardHead}>
          <span className={styles.cardName}>{c.name}</span>
          {!c.enable ? <Pill tone="off">Disabled</Pill> : failing ? <Pill tone="error">Failing</Pill> : null}
        </span>
        <span className={styles.cardType}>{c.implementationName}</span>
        <span className={styles.triggers}>
          {triggers.length === 0 ? (
            <span className={ui.muted}>No triggers</span>
          ) : (
            triggers.map((t) => (
              <span key={t} className={styles.trigger}>
                {triggerLabels[t]}
              </span>
            ))
          )}
        </span>
        {failing && (
          <span className={styles.cardError}>
            {c.status.lastError}
            {c.status.lastFailure && <span className={styles.cardTime}> · {formatTime(c.status.lastFailure)}</span>}
          </span>
        )}
      </button>
    </li>
  )
}
