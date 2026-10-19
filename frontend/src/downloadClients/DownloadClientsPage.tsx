import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router'
import { ApiError } from '../api/client'
import {
  builtInImplementation,
  clientAddress,
  downloadClientKeys,
  downloadClientsApi,
  newClient,
  type ClientImplementation,
  type DownloadClient,
} from '../api/downloadClients'
import { downloadSettingsApi, downloadSettingsKeys } from '../api/downloadSettings'
import { usenetApi, usenetKeys } from '../api/usenet'
import Icon from '../components/Icon'
import { Skeleton } from '../components/Loader'
import ui from '../components/ui.module.css'
import { Pill } from '../indexers/Chips'
import { Section, SettingsPage, Toggle } from '../settings/SettingsKit'
import { useIsAdmin } from '../status/useOngoing'
import ClientForm from './ClientForm'
import { ClientMark } from './ClientMark'
import { EngineSection } from './EngineSection'
import ImplementationPicker from './ImplementationPicker'
import { RemotePathMappings } from './RemotePathMappings'
import { useTurnOnBuiltIn } from './useBuiltIn'
import styles from './DownloadClients.module.css'

type View = { kind: 'none' } | { kind: 'pick' } | { kind: 'form'; client: DownloadClient; type: ClientImplementation; fromPicker: boolean }

/**
 * Settings → Download clients (0.11): Usenet stays in StreamZH's own engine (or the blackhole folder); torrents go to
 * qBittorrent, Transmission, Deluge or the built-in torrent engine. Remote path mappings and the engine's settings below.
 */
export default function DownloadClientsPage() {
  const canEdit = useIsAdmin()
  const clients = useQuery({ queryKey: downloadClientKeys.list, queryFn: downloadClientsApi.list, refetchInterval: 30_000 })
  const schema = useQuery({ queryKey: downloadClientKeys.schema, queryFn: downloadClientsApi.schema, staleTime: 10 * 60_000 })
  const [view, setView] = useState<View>({ kind: 'none' })
  const close = () => setView({ kind: 'none' })

  const list = clients.data ?? []
  const types = schema.data ?? []
  const typeOf = (implementation: string) => types.find((t) => t.implementation.toLowerCase() === implementation.toLowerCase())
  const edit = (client: DownloadClient) => {
    const type = typeOf(client.implementation)
    if (type) setView({ kind: 'form', client, type, fromPicker: false })
  }
  const builtIn = typeOf(builtInImplementation) ?? null
  const missing = clients.error instanceof ApiError && clients.error.status === 404
  const turnOn = useTurnOnBuiltIn(builtIn, list)

  return (
    <SettingsPage
      title="Download clients"
      lead="Where grabbed releases are downloaded. Usenet runs in StreamZH itself; torrents go to the built-in engine or to qBittorrent, Transmission or Deluge."
    >
      <UsenetSection />

      <Section
        id="torrent-clients"
        title="Torrent clients"
        description="Torznab indexers need one. New torrents go to the enabled client with the highest priority; StreamZH follows them in Activity and imports them without breaking the seed."
      >
        {clients.isPending && <Skeleton rows={2} />}
        {clients.isError && <p className={ui.error}>{missing ? 'This StreamZH build does not support torrent clients yet.' : clients.error.message}</p>}
        {clients.isSuccess && (
          <TorrentClients
            clients={list.filter((c) => c.protocol === 'torrent')}
            canEdit={canEdit}
            schemaReady={schema.isSuccess}
            onAdd={() => setView({ kind: 'pick' })}
            onEdit={edit}
            builtIn={builtIn}
            turningOn={turnOn.isPending}
            turnOnError={turnOn.error?.message ?? null}
            onUseBuiltIn={() => turnOn.mutate()}
          />
        )}
      </Section>

      {clients.isSuccess && schema.isSuccess && <EngineSection type={builtIn} clients={list} canEdit={canEdit} onEdit={edit} />}
      {clients.isSuccess && <RemotePathMappings clients={list} canEdit={canEdit} />}

      {view.kind === 'pick' && (
        <ImplementationPicker
          types={types}
          existing={list}
          onClose={close}
          onSelect={(type) => setView({ kind: 'form', client: newClient(type, list), type, fromPicker: true })}
        />
      )}
      {view.kind === 'form' && (
        <ClientForm
          key={`${view.client.id}-${view.type.implementation}`}
          client={view.client}
          type={view.type}
          canEdit={canEdit}
          onClose={close}
          onBack={view.fromPicker ? () => setView({ kind: 'pick' }) : undefined}
        />
      )}
    </SettingsPage>
  )
}

/** Usenet: the built-in engine (or the blackhole folder) and the servers; edited on their own pages. */
function UsenetSection() {
  const settings = useQuery({ queryKey: downloadSettingsKeys.settings, queryFn: downloadSettingsApi.get })
  const servers = useQuery({ queryKey: usenetKeys.servers, queryFn: usenetApi.list })
  const blackhole = settings.data?.client === 'blackhole'
  const enabled = (servers.data ?? []).filter((s) => s.enabled).length
  return (
    <Section id="usenet" title="Usenet" description="NZBs from Newznab indexers. StreamZH downloads, repairs and unpacks them itself, like SABnzbd.">
      <div className={styles.usenet}>
        <ClientMark implementation={blackhole ? 'usenet' : 'streamzh'} size={36} />
        <div className={styles.usenetText}>
          <span className={styles.usenetName}>{blackhole ? 'Blackhole folder' : 'StreamZH built-in engine'}</span>
          <span className={styles.usenetSub}>
            {settings.isPending
              ? 'Loading…'
              : blackhole
                ? `NZB files go to ${settings.data?.nzbFolder ?? 'a folder'} for another downloader`
                : servers.isSuccess
                  ? enabled === 0
                    ? 'No Usenet server yet'
                    : `${enabled} Usenet server${enabled === 1 ? '' : 's'}`
                  : 'Downloads with your Usenet servers'}
          </span>
        </div>
        {!blackhole && servers.isSuccess && enabled === 0 ? <Pill tone="warn">Needs a server</Pill> : <Pill tone="ok">Active</Pill>}
        <div className={styles.usenetLinks}>
          <Link className={`${ui.button} ${ui.sm}`} to="/settings/downloads">
            Settings → Downloads
          </Link>
          {!blackhole && (
            <Link className={`${ui.button} ${ui.sm}`} to="/settings/usenet">
              Usenet servers
            </Link>
          )}
        </div>
      </div>
    </Section>
  )
}

function TorrentClients({
  clients,
  canEdit,
  schemaReady,
  builtIn,
  turningOn,
  turnOnError,
  onAdd,
  onEdit,
  onUseBuiltIn,
}: {
  clients: DownloadClient[]
  canEdit: boolean
  schemaReady: boolean
  builtIn: ClientImplementation | null
  turningOn: boolean
  turnOnError: string | null
  onAdd: () => void
  onEdit: (client: DownloadClient) => void
  onUseBuiltIn: () => void
}) {
  const sorted = [...clients].sort((a, b) => Number(b.enable) - Number(a.enable) || a.priority - b.priority || a.name.localeCompare(b.name))
  const first = sorted.find((c) => c.enable)

  if (clients.length === 0) {
    return (
      <div className={styles.empty}>
        <p>
          <b>No torrent client yet.</b> Releases from Torznab indexers cannot be downloaded until you add one.
        </p>
        {canEdit && (
          <div className={styles.emptyActions}>
            {builtIn && (
              <button type="button" className={ui.primary} onClick={onUseBuiltIn} disabled={turningOn}>
                <Icon name="power" size={15} />
                {turningOn ? 'Turning on…' : 'Use the built-in engine'}
              </button>
            )}
            <button type="button" className={ui.button} onClick={onAdd} disabled={!schemaReady}>
              <Icon name="plus" size={15} />
              Add qBittorrent, Transmission or Deluge
            </button>
          </div>
        )}
        {turnOnError && <p className={ui.error}>{turnOnError}</p>}
      </div>
    )
  }

  return (
    <ul className={styles.grid}>
      {sorted.map((c) => (
        <ClientCard key={c.id} client={c} canEdit={canEdit} isDefault={c.id === first?.id} onEdit={() => onEdit(c)} />
      ))}
      {canEdit && (
        <li>
          <button className={styles.addCard} type="button" onClick={onAdd} disabled={!schemaReady}>
            <span className={styles.plus} aria-hidden="true">
              <Icon name="plus" size={16} />
            </span>
            Add torrent client
          </button>
        </li>
      )}
    </ul>
  )
}

function ClientCard({ client: c, canEdit, isDefault, onEdit }: { client: DownloadClient; canEdit: boolean; isDefault: boolean; onEdit: () => void }) {
  const queryClient = useQueryClient()
  const toggle = useMutation({
    mutationFn: (enable: boolean) => downloadClientsApi.update({ ...c, enable }),
    onSettled: () => queryClient.invalidateQueries({ queryKey: downloadClientKeys.all }),
  })
  const failing = c.enable && c.status != null && !c.status.healthy
  const address = c.implementation === builtInImplementation ? 'Inside StreamZH' : clientAddress(c)
  const category = typeof c.settings.category === 'string' && c.settings.category ? c.settings.category : null
  const state = !c.enable ? styles.disabled : failing ? styles.failing : ''

  return (
    <li>
      <article className={`${styles.card} ${state}`}>
        <button type="button" className={styles.cardMain} onClick={onEdit} aria-label={`${canEdit ? 'Edit' : 'Show'} ${c.name}`}>
          <span className={styles.cardHead}>
            <ClientMark implementation={c.implementation} />
            <span className={styles.cardTitle}>
              <span className={styles.cardName}>{c.name}</span>
              <span className={styles.cardType}>
                {c.implementationName}
                {c.status?.version ? ` ${c.status.version}` : ''}
              </span>
            </span>
            {!c.enable ? <Pill tone="off">Disabled</Pill> : failing ? <Pill tone="error">Unreachable</Pill> : isDefault ? <Pill tone="ok">Default</Pill> : null}
          </span>
          <span className={styles.cardFacts}>
            {address && (
              <span className={styles.fact}>
                <Icon name={c.implementation === builtInImplementation ? 'box' : 'server'} size={13} />
                <span className={styles.factText}>{address}</span>
              </span>
            )}
            {category && (
              <span className={styles.fact}>
                <Icon name="tag" size={13} />
                <span className={styles.factText}>{category}</span>
              </span>
            )}
            <span className={styles.fact}>Priority {c.priority}</span>
            {c.removeCompleted && <span className={styles.fact}>Removes completed</span>}
          </span>
          {failing && c.status?.message && <span className={styles.cardError}>{c.status.message}</span>}
        </button>
        {canEdit && (
          <div className={styles.cardFoot}>
            <Toggle checked={c.enable} onChange={(enable) => toggle.mutate(enable)} disabled={toggle.isPending} label={`${c.name} enabled`} on="Enabled" off="Disabled" />
            {toggle.error && <span className={ui.error}>{toggle.error.message}</span>}
          </div>
        )}
      </article>
    </li>
  )
}
