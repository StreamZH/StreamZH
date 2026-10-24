import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { useSearchParams } from 'react-router'
import { useToasts } from '../activity/hooks'
import {
  genericDefinitionKey,
  indexerKeys,
  indexersApi,
  protocolOf,
  type Indexer,
  type IndexerCapabilities,
  type IndexerDefinition,
} from '../api/indexers'
import { useAuth } from '../auth/auth'
import Icon from '../components/Icon'
import { Skeleton } from '../components/Loader'
import PageHeader from '../components/PageHeader'
import ui from '../components/ui.module.css'
import Toasts from '../indexerTools/Toast'
import Catalog from '../indexers/Catalog'
import { Pill, PrivacyChip, ProtocolChip } from '../indexers/Chips'
import IndexerDetails from '../indexers/IndexerDetails'
import IndexerForm from '../indexers/IndexerForm'
import {
  cloneOf,
  draftFromDefinition,
  formatUntil,
  grabUsage,
  hostOf,
  missingApiKey,
  presetAddress,
  queryUsage,
  rowState,
  throughProwlarr,
  usesList,
  vipState,
  type Usage,
} from '../indexers/model'
import ProwlarrImport from '../indexers/ProwlarrImport'
import TestAll from '../indexers/TestAll'
import styles from './Indexers.module.css'

type View =
  | { kind: 'none' }
  | { kind: 'catalog' }
  | {
      kind: 'form'
      indexer: Indexer
      definition: IndexerDefinition | null
      capabilities?: IndexerCapabilities | null
      fromCatalog?: boolean
      /** Opened by "Use <host>" in the details: moved away from Prowlarr's proxy, the key still has to be entered. */
      movedFromProwlarr?: boolean
    }
  | { kind: 'details'; id: number }
  | { kind: 'testAll' }
  | { kind: 'import' }

const schemaKey = ['indexer-schema'] as const

/** Indexers (the hub tabs Indexers / Search / Stats / History come from the shell): the list, enable switches, tests and the dialogs. */
export default function Indexers() {
  const { user } = useAuth()
  const canEdit = user?.role === 'Owner' || user?.role === 'Admin'
  const queryClient = useQueryClient()
  const indexers = useQuery({ queryKey: indexerKeys.list, queryFn: indexersApi.list, refetchInterval: 60_000 })
  // For protocol and privacy chips; the list still works when the catalog cannot be loaded.
  const definitions = useQuery({ queryKey: indexerKeys.definitions, queryFn: indexersApi.definitions, staleTime: 10 * 60_000, retry: 1 })
  // Links from "Get started" (1.0 RC): ?add=1 opens the catalog, ?import=prowlarr the Prowlarr import.
  const [params, setParams] = useSearchParams()
  const [view, setView] = useState<View>(() =>
    !canEdit ? { kind: 'none' } : params.get('import') === 'prowlarr' ? { kind: 'import' } : params.get('add') ? { kind: 'catalog' } : { kind: 'none' },
  )
  const close = () => {
    setView({ kind: 'none' })
    if (params.has('add') || params.has('import')) setParams({}, { replace: true })
  }
  const { toasts, push, dismiss } = useToasts()

  const byKey = useMemo(() => new Map((definitions.data ?? []).map((d) => [d.key, d])), [definitions.data])
  const definitionOf = (i: Indexer) => byKey.get(i.definitionKey ?? genericDefinitionKey) ?? null

  const pick = useMutation({
    mutationFn: async (definition: IndexerDefinition) => {
      const schema = await queryClient.fetchQuery({ queryKey: schemaKey, queryFn: indexersApi.schema, staleTime: 10 * 60_000 })
      return { schema, definition }
    },
    onSuccess: ({ schema, definition }) =>
      setView({ kind: 'form', indexer: draftFromDefinition(schema, definition), definition, fromCatalog: true }),
  })

  // The enable switch saves the indexer as it is with the other value (the masked API key keeps the stored key).
  const toggle = useMutation({
    mutationFn: (i: Indexer) => indexersApi.update({ ...i, enable: !i.enable }),
    onSuccess: (_, i) => push('ok', `${i.name} ${i.enable ? 'disabled' : 'enabled'}`),
    onError: (e, i) => push('error', `Could not change ${i.name}`, e.message),
    onSettled: () => queryClient.invalidateQueries({ queryKey: indexerKeys.all }),
  })
  // One indexer's connection test (the stored API key is used).
  const test = useMutation({
    mutationFn: (i: Indexer) => indexersApi.test(i),
    onSuccess: (caps, i) =>
      push('ok', `${i.name} works`, caps.categories.length > 0 ? `${caps.categories.length} category groups loaded` : undefined),
    onError: (e, i) => push('error', `${i.name} failed the test`, e.message),
    onSettled: () => queryClient.invalidateQueries({ queryKey: indexerKeys.all }),
  })

  const list = indexers.data ?? []
  const enabled = list.filter((i) => i.enable).length
  const failing = list.filter((i) => rowState(i) === 'error').length

  const actions = canEdit && (
    <>
      <button className={ui.button} type="button" onClick={() => setView({ kind: 'import' })}>
        <Icon name="download" size={15} /> Import from Prowlarr
      </button>
      {list.length > 0 && (
        <button className={ui.button} type="button" onClick={() => setView({ kind: 'testAll' })}>
          <Icon name="zap" size={15} /> Test all
        </button>
      )}
      <button className={ui.primary} type="button" onClick={() => setView({ kind: 'catalog' })}>
        <Icon name="plus" size={15} /> Add indexer
      </button>
    </>
  )

  const torrentCount = list.filter((i) => protocolOf(i) === 'torrent').length
  const lead =
    list.length > 0 ? (
      <span className={styles.lead}>
        {torrentCount === 0 ? (
          <span>
            <b>{list.length}</b> Newznab indexer{list.length === 1 ? '' : 's'}
          </span>
        ) : (
          <span>
            <b>{list.length - torrentCount}</b> Newznab · <b>{torrentCount}</b> Torznab
          </span>
        )}
        <span>
          <span className={ui.dotOk} /> <b>{enabled}</b> enabled
        </span>
        {failing > 0 && (
          <span className={styles.leadErr}>
            <span className={ui.dotErr} /> <b>{failing}</b> failing
          </span>
        )}
      </span>
    ) : (
      'Where StreamZH searches for releases. Failing indexers are skipped for a while and tried again later.'
    )

  return (
    <div className={styles.page}>
      <PageHeader title="Indexers" lead={lead} actions={actions || undefined} />

      {indexers.isError && <p className={ui.errBox}>{indexers.error.message}</p>}
      {pick.error && <p className={ui.errBox}>{pick.error.message}</p>}

      {indexers.isPending && (
        <div className={ui.panel}>
          <Skeleton rows={4} />
        </div>
      )}

      {indexers.isSuccess && list.length === 0 && (
        <div className={ui.panel}>
          <div className={ui.empty}>
            <span className={styles.emptyIcon}>
              <Icon name="network" size={20} />
            </span>
            <h3>No indexers yet</h3>
            <p className={styles.emptyText}>
              StreamZH searches Usenet indexers through <strong>Newznab</strong> and torrent indexers through <strong>Torznab</strong> (Jackett,
              Prowlarr or the tracker itself). Pick yours from the list of known indexers, or bring them all over from Prowlarr in one go.
            </p>
            {canEdit ? (
              <div className={styles.emptyActions}>
                <button className={`${ui.button} ${ui.sm}`} type="button" onClick={() => setView({ kind: 'import' })}>
                  Import from Prowlarr
                </button>
                <button className={`${ui.primary} ${ui.sm}`} type="button" onClick={() => setView({ kind: 'catalog' })}>
                  <Icon name="plus" size={14} /> Add indexer
                </button>
              </div>
            ) : (
              <p className={ui.muted}>An admin can add indexers.</p>
            )}
          </div>
        </div>
      )}

      {list.length > 0 && (
        <div className={`${ui.panel} ${styles.tablePanel}`}>
          <table className={`${ui.table} ${styles.table}`}>
            <thead>
              <tr>
                {canEdit && (
                  <th className={styles.switchCol}>
                    <span className={styles.srOnly}>Enabled</span>
                  </th>
                )}
                <th>Indexer</th>
                <th className={`${styles.r} ${styles.hideSm}`}>Priority</th>
                <th className={styles.hideMd}>Used for</th>
                <th className={styles.hideSm}>Queries</th>
                <th className={styles.hideSm}>Grabs</th>
                <th>
                  <span className={styles.srOnly}>Actions</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {list.map((i) => (
                <IndexerRow
                  key={i.id}
                  indexer={i}
                  definition={definitionOf(i)}
                  canEdit={canEdit}
                  toggling={toggle.isPending && toggle.variables?.id === i.id}
                  testing={test.isPending && test.variables?.id === i.id}
                  onToggle={() => toggle.mutate(i)}
                  onTest={() => test.mutate(i)}
                  onOpen={() => setView({ kind: 'details', id: i.id })}
                  onEdit={() => setView({ kind: 'form', indexer: i, definition: definitionOf(i) })}
                />
              ))}
            </tbody>
          </table>
        </div>
      )}

      {view.kind === 'catalog' && (
        <Catalog existing={list} onSelect={(d) => pick.mutate(d)} onImport={() => setView({ kind: 'import' })} onClose={close} />
      )}
      {view.kind === 'form' && (
        <IndexerForm
          key={`${view.indexer.id}-${view.indexer.name}`}
          indexer={view.indexer}
          definition={view.definition}
          capabilities={view.capabilities ?? null}
          canEdit={canEdit}
          onClose={close}
          onBack={view.fromCatalog ? () => setView({ kind: 'catalog' }) : undefined}
          movedFromProwlarr={view.movedFromProwlarr}
        />
      )}
      {view.kind === 'details' && (
        <IndexerDetails
          id={view.id}
          fallback={list.find((i) => i.id === view.id) ?? null}
          canEdit={canEdit}
          onClose={close}
          onEdit={(d, moved) =>
            // Moved to its own address: the capabilities read through Prowlarr no longer apply.
            setView({
              kind: 'form',
              indexer: moved ?? d.indexer,
              definition: d.definition,
              capabilities: moved ? null : d.capabilities,
              movedFromProwlarr: moved != null,
            })
          }
          onClone={(d) => setView({ kind: 'form', indexer: cloneOf(d.indexer), definition: d.definition, capabilities: d.capabilities })}
        />
      )}
      {view.kind === 'testAll' && <TestAll onClose={close} onOpen={(id) => setView({ kind: 'details', id })} />}
      {view.kind === 'import' && <ProwlarrImport onClose={close} onTestAll={() => setView({ kind: 'testAll' })} />}
      <Toasts toasts={toasts} onDismiss={dismiss} />
    </div>
  )
}

/** Queries or grabs used in the current window; with a limit, a slim CloudZH .bar shows how full it is. */
function Meter({ usage }: { usage: Usage }) {
  if (usage.ratio == null) return <span className={styles.usage}>{usage.text}</span>
  const tone = usage.ratio >= 1 ? styles.full : usage.ratio >= 0.8 ? styles.high : ''
  return (
    <span className={`${styles.usage} ${tone}`}>
      {usage.text}
      <span className={styles.meter} aria-hidden="true">
        <i style={{ width: `${Math.max(2, Math.round(usage.ratio * 100))}%` }} />
      </span>
    </span>
  )
}

function IndexerRow({
  indexer: i,
  definition,
  canEdit,
  toggling,
  testing,
  onToggle,
  onTest,
  onOpen,
  onEdit,
}: {
  indexer: Indexer
  definition: IndexerDefinition | null
  canEdit: boolean
  toggling: boolean
  testing: boolean
  onToggle: () => void
  onTest: () => void
  onOpen: () => void
  onEdit: () => void
}) {
  const state = rowState(i)
  const status = i.status
  const vip = vipState(status)
  const uses = usesList(i)
  const dot = state === 'enabled' ? ui.dotOk : state === 'error' ? ui.dotErr : ui.dot
  const stateText = state === 'enabled' ? 'Working' : state === 'disabled' ? 'Disabled' : 'Failing'

  return (
    <tr className={`${styles.row} ${state === 'disabled' ? styles.disabled : ''}`} onClick={onOpen}>
      {canEdit && (
        <td className={styles.switchCol} onClick={(e) => e.stopPropagation()}>
          <label className={ui.switch} title={i.enable ? 'Enabled' : 'Disabled'}>
            <input type="checkbox" role="switch" checked={i.enable} disabled={toggling} onChange={onToggle} aria-label={`${i.name} enabled`} />
            <span />
          </label>
        </td>
      )}
      <td className={styles.main}>
        <div className={styles.nameLine}>
          <span className={dot} title={stateText} aria-label={stateText} role="img" />
          <button
            type="button"
            className={styles.name}
            onClick={(e) => {
              e.stopPropagation()
              onOpen()
            }}
          >
            {i.name}
          </button>
          <ProtocolChip protocol={protocolOf(i)} />
          {definition && <PrivacyChip privacy={definition.privacy} />}
        </div>
        <div className={styles.badges}>
          <span className={styles.host}>{hostOf(i.baseUrl)}</span>
          <span className={styles.showSm}>Priority {i.priority}</span>
          {i.enable && status && !status.healthy && status.disabledTill && <Pill tone="error">Skipped until {formatUntil(status.disabledTill)}</Pill>}
          {status?.limitReached && <Pill tone="error">{status.limitReached === 'query' ? 'Query limit reached' : 'Grab limit reached'}</Pill>}
          {vip && <Pill tone={vip.tone}>{vip.text}</Pill>}
          {throughProwlarr(i) &&
            (presetAddress(definition) ? (
              <Pill tone="warn" title="Reached through Prowlarr instead of the indexer's own address. Open it to switch.">
                Via Prowlarr
              </Pill>
            ) : (
              <Pill tone="info" title="Searched through Prowlarr (for site scrapers without an API of their own, Prowlarr has to keep running).">
                Via Prowlarr
              </Pill>
            ))}
          {canEdit && !i.enable && missingApiKey(i) && !throughProwlarr(i) && (
            <Pill tone="warn" title="Enter the indexer's API key under Edit, then switch it on.">
              API key missing
            </Pill>
          )}
        </div>
        {status?.lastError && (
          <p className={status.healthy ? styles.lastWarn : styles.lastError}>
            <Icon name="alert" size={13} />
            <span>
              {status.healthy ? 'Last error' : 'Error'}: {status.lastError}
            </span>
          </p>
        )}
      </td>
      <td className={`${styles.r} ${styles.hideSm}`}>
        <span className={styles.priority}>{i.priority}</span>
      </td>
      <td className={`${styles.hideMd} ${styles.uses}`}>
        {!i.enable ? (
          <span className={ui.muted}>–</span>
        ) : uses.length > 0 ? (
          <span className={styles.useTags}>
            {uses.map((u) => (
              <span key={u} className={ui.tag}>
                {u}
              </span>
            ))}
          </span>
        ) : (
          <span className={ui.muted}>Nothing</span>
        )}
      </td>
      <td className={styles.hideSm}>
        <Meter usage={queryUsage(i)} />
      </td>
      <td className={styles.hideSm}>
        <Meter usage={grabUsage(i)} />
      </td>
      <td className={styles.actions} onClick={(e) => e.stopPropagation()}>
        {canEdit && (
          <>
            <button className={`${ui.ghost} ${ui.sm}`} type="button" onClick={onTest} disabled={testing} title={`Test ${i.name}`}>
              <Icon name="zap" size={14} className={testing ? styles.spin : undefined} />
              <span className={styles.btnText}>{testing ? 'Testing…' : 'Test'}</span>
            </button>
            <button className={`${ui.ghost} ${ui.sm}`} type="button" onClick={onEdit} title={`Edit ${i.name}`}>
              <Icon name="edit" size={14} />
              <span className={styles.btnText}>Edit</span>
            </button>
          </>
        )}
      </td>
    </tr>
  )
}
