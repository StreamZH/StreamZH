import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { ApiError } from '../api/client'
import {
  builtInImplementation,
  downloadClientKeys,
  downloadClientsApi,
  type ClientImplementation,
  type ClientValue,
  type DownloadClient,
  type EngineStatus,
  type TorrentEngineSettings,
} from '../api/downloadClients'
import FolderPicker from '../components/FolderPicker'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import { AdvancedHint } from '../lib/Advanced'
import { formatSpeed } from '../status/format'
import { NumberInput, Row, SaveBar, Section, Select, Toggle } from '../settings/SettingsKit'
import { useSettingsForm } from '../settings/useSettingsForm'
import { engineFields, type EngineField } from './engineFields'
import { useTurnOnBuiltIn } from './useBuiltIn'
import styles from './DownloadClients.module.css'

/**
 * The built-in torrent engine: off until the user turns it on (that adds the "StreamZH" client), then its settings.
 * Simple: folder, port, speed limits and default seed goals. Advanced: connections, DHT/PEX/LSD, encryption, UPnP …
 */
export function EngineSection({
  type,
  clients,
  canEdit,
  onEdit,
}: {
  /** The built-in implementation from the schema; null when this server has none. */
  type: ClientImplementation | null
  clients: DownloadClient[]
  canEdit: boolean
  onEdit: (client: DownloadClient) => void
}) {
  const queryClient = useQueryClient()
  const row = clients.find((c) => c.implementation === builtInImplementation) ?? null
  const refresh = () => queryClient.invalidateQueries({ queryKey: downloadClientKeys.all })
  const turnOn = useTurnOnBuiltIn(type, clients)
  const toggle = useMutation({ mutationFn: (enable: boolean) => downloadClientsApi.update({ ...row!, enable }), onSuccess: refresh })
  const status = useQuery({
    queryKey: downloadClientKeys.engineStatus,
    queryFn: downloadClientsApi.engineStatus,
    enabled: row != null,
    retry: false,
    refetchInterval: row?.enable ? 5_000 : 30_000,
  })

  if (!type) return null
  const on = row?.enable ?? false

  return (
    <Section
      id="engine"
      title="Built-in torrent engine"
      description="StreamZH downloads and seeds torrents itself, like it does Usenet. Nothing else to install; open the listening port for the best speed."
      actions={
        row && canEdit ? (
          <button type="button" className={`${ui.ghost} ${ui.sm}`} onClick={() => onEdit(row)}>
            <Icon name="edit" size={14} />
            Client settings
          </button>
        ) : undefined
      }
    >
      <Row
        label="Status"
        help={row ? (on ? 'Gets torrents when it has the highest priority of the enabled torrent clients.' : 'Turned off. Torrents in it pause until you turn it on again.') : 'Off. Turn it on to download torrents without an external client.'}
        error={turnOn.error?.message ?? toggle.error?.message}
      >
        {row ? (
          <div className={styles.engineState}>
            <Toggle checked={on} onChange={(enable) => toggle.mutate(enable)} disabled={!canEdit || toggle.isPending} on="On" off="Off" />
            {on && <EnginePill status={status.data} />}
            <span className={ui.muted}>Priority {row.priority}</span>
          </div>
        ) : (
          <button type="button" className={ui.button} onClick={() => turnOn.mutate()} disabled={!canEdit || turnOn.isPending}>
            <Icon name="power" size={15} />
            {turnOn.isPending ? 'Turning on…' : 'Turn on'}
          </button>
        )}
      </Row>
      {row && status.data && <EngineFacts status={status.data} />}
      {row && <EngineSettings canEdit={canEdit} />}
    </Section>
  )
}

/** Running / port busy / starting, from the engine's live status (none on servers without it). */
function EnginePill({ status }: { status: EngineStatus | undefined }) {
  if (!status) return <span className={`${ui.pill} ${ui.pillOk}`}>On</span>
  if (status.startError || status.portState === 'notFree')
    return <span className={`${ui.pill} ${ui.pillErr}`}>{status.portState === 'notFree' ? 'Port in use' : 'Not running'}</span>
  if (!status.running) return <span className={`${ui.pill} ${ui.pillBusy}`}>Starting…</span>
  return <span className={`${ui.pill} ${ui.pillOk}`}>{status.portState === 'listening' ? 'Listening' : 'Running'}</span>
}

/** "Port 51413 · 3 torrents, 1 active · ↓ 2.1 MB/s ↑ 340 KB/s", the start error and torrent problems (disk full, tracker errors). */
function EngineFacts({ status }: { status: EngineStatus }) {
  const notFree = status.portState === 'notFree'
  return (
    <div className={styles.engineFacts}>
      <span className={styles.engineLine}>
        <span>Port {status.listenPort}</span>
        <span>
          {status.torrents} torrent{status.torrents === 1 ? '' : 's'}, {status.active} active
        </span>
        <span>
          <Icon name="download" size={13} />
          {formatSpeed(status.downloadSpeed)}
        </span>
        <span>
          <Icon name="upload" size={13} />
          {formatSpeed(status.uploadSpeed)}
        </span>
        {status.engineVersion && <span className={ui.muted}>MonoTorrent {status.engineVersion}</span>}
      </span>
      {(status.startError || notFree) && (
        <p className={styles.engineProblem}>
          <Icon name="alert" size={14} />
          {status.startError ?? `Another program uses port ${status.listenPort}. Choose another listening port below.`}
        </p>
      )}
      {status.problems.slice(0, 5).map((p) => (
        <p key={`${p.hash}-${p.kind}`} className={styles.engineWarn}>
          <Icon name="alert" size={14} />
          <span>
            <b>{p.name}</b>: {p.message}
          </span>
        </p>
      ))}
    </div>
  )
}

function EngineSettings({ canEdit }: { canEdit: boolean }) {
  const form = useSettingsForm<TorrentEngineSettings>({
    queryKey: downloadClientKeys.engine,
    load: downloadClientsApi.engine,
    save: downloadClientsApi.saveEngine,
  })
  const v = form.values
  if (form.query.isError) {
    const missing = form.query.error instanceof ApiError && form.query.error.status === 404
    return <p className={styles.sectionNote}>{missing ? 'This build has no engine settings yet.' : form.query.error.message}</p>
  }
  if (!v) return null

  const fields = engineFields(v)
  const hidden = fields.filter((f) => f.advanced).length
  return (
    <>
      {fields.map((f) => (
        <EngineRow key={f.key} field={f} value={v[f.key] ?? null} error={form.error(f.key)} disabled={!canEdit} onChange={(value) => form.set({ [f.key]: value })} />
      ))}
      <AdvancedHint count={hidden} />
      <SaveBar dirty={form.dirty} saving={form.saving} saved={form.saved} error={form.formError} onSave={form.save} onReset={form.reset} disabled={!canEdit} />
    </>
  )
}

function EngineRow({ field: f, value, error, disabled, onChange }: { field: EngineField; value: ClientValue; error?: string; disabled: boolean; onChange: (value: ClientValue) => void }) {
  const id = `engine-${f.key}`
  let control
  switch (f.type) {
    case 'bool':
      control = <Toggle checked={value === true} onChange={onChange} disabled={disabled} />
      break
    case 'number':
      control = <NumberInput id={id} value={typeof value === 'number' ? value : null} onChange={onChange} unit={f.unit} min={f.min} max={f.max} step={f.step} />
      break
    case 'select': {
      const options = f.options ?? []
      const text = value == null ? '' : String(value)
      control = (
        <Select
          id={id}
          value={text}
          onChange={onChange}
          options={options.some((o) => o.value === text) ? options : [...options, { value: text, label: text || 'Default' }]}
        />
      )
      break
    }
    case 'path':
      control = <EnginePath id={id} value={value == null ? '' : String(value)} onChange={onChange} />
      break
    default:
      control = <input id={id} className={ui.input} value={value == null ? '' : String(value)} onChange={(e) => onChange(e.target.value)} spellCheck={false} />
  }
  return (
    <Row label={f.label} htmlFor={f.type === 'bool' ? undefined : id} help={f.help} advanced={f.advanced} error={error}>
      {control}
    </Row>
  )
}

function EnginePath({ id, value, onChange }: { id: string; value: string; onChange: (value: string) => void }) {
  const [picking, setPicking] = useState(false)
  return (
    <div className={styles.pathRow}>
      <input id={id} className={`${ui.input} ${ui.mono}`} value={value} spellCheck={false} placeholder="Complete folder" onChange={(e) => onChange(e.target.value)} />
      <button className={ui.button} type="button" onClick={() => setPicking(true)}>
        Browse…
      </button>
      {picking && (
        <FolderPicker
          initialPath={value}
          onClose={() => setPicking(false)}
          onSelect={(selected) => {
            onChange(selected)
            setPicking(false)
          }}
        />
      )}
    </div>
  )
}
