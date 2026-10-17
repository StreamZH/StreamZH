import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import {
  genericDefinitionKey,
  indexerKeys,
  indexersApi,
  type Indexer,
  type IndexerCapabilities,
  type IndexerDefinition,
} from '../api/indexers'
import { useAuth } from '../auth/auth'
import ui from '../components/ui.module.css'
import Catalog from '../indexers/Catalog'
import { Pill, PrivacyChip, ProtocolChip } from '../indexers/Chips'
import IndexerDetails from '../indexers/IndexerDetails'
import IndexerForm from '../indexers/IndexerForm'
import { cloneOf, draftFromDefinition, formatUntil, grabUsage, hostOf, queryUsage, rowState, usesList, vipState, type Usage } from '../indexers/model'
import ProwlarrImport from '../indexers/ProwlarrImport'
import TestAll from '../indexers/TestAll'
import page from './Page.module.css'
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
    }
  | { kind: 'details'; id: number }
  | { kind: 'testAll' }
  | { kind: 'import' }

const schemaKey = ['indexer-schema'] as const

export default function Indexers() {
  const { user } = useAuth()
  const canEdit = user?.role === 'Owner' || user?.role === 'Admin'

  return (
    <div className={page.page}>
      <header>
        <h1 className={page.title}>Indexers</h1>
        <p className={page.lead}>Where StreamZH searches for releases. Failing indexers are skipped for a while and tried again later.</p>
      </header>
      <IndexerList canEdit={canEdit} />
    </div>
  )
}

function IndexerList({ canEdit }: { canEdit: boolean }) {
  const queryClient = useQueryClient()
  const indexers = useQuery({ queryKey: indexerKeys.list, queryFn: indexersApi.list, refetchInterval: 60_000 })
  // For protocol and privacy chips; the list still works when the catalog cannot be loaded.
  const definitions = useQuery({ queryKey: indexerKeys.definitions, queryFn: indexersApi.definitions, staleTime: 10 * 60_000, retry: 1 })
  const [view, setView] = useState<View>({ kind: 'none' })
  const close = () => setView({ kind: 'none' })

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

  const list = indexers.data ?? []
  const enabled = list.filter((i) => i.enable).length

  const toolbar = canEdit && (
    <div className={styles.toolbar}>
      <button className={ui.button} type="button" onClick={() => setView({ kind: 'import' })}>
        Import from Prowlarr
      </button>
      {list.length > 0 && (
        <button className={ui.button} type="button" onClick={() => setView({ kind: 'testAll' })}>
          Test all
        </button>
      )}
      <button className={ui.primary} type="button" onClick={() => setView({ kind: 'catalog' })}>
        Add indexer
      </button>
    </div>
  )

  return (
    <section className={page.panel}>
      <div className={styles.panelHead}>
        <div>
          <h2 className={page.panelTitle}>Newznab indexers</h2>
          {list.length > 0 && (
            <p className={styles.summary}>
              {list.length} indexer{list.length === 1 ? '' : 's'} · {enabled} enabled
            </p>
          )}
        </div>
        {toolbar}
      </div>
      {indexers.isError && <p className={ui.error}>{indexers.error.message}</p>}
      {pick.error && <p className={ui.error}>{pick.error.message}</p>}

      {indexers.isSuccess && list.length === 0 && (
        <div className={styles.empty}>
          <p>
            StreamZH searches Usenet indexers through <strong>Newznab</strong>, the API most Usenet indexers offer. Pick yours from the list of
            known indexers, or bring them all over from Prowlarr in one go.
          </p>
          {!canEdit && <p className={ui.muted}>An admin can add indexers.</p>}
        </div>
      )}

      {list.length > 0 && (
        <div className={styles.table}>
          <div className={styles.head} aria-hidden="true">
            <span>Indexer</span>
            <span>Priority</span>
            <span>Used for</span>
            <span>Queries</span>
            <span>Grabs</span>
            <span />
          </div>
          <ul className={styles.list}>
            {list.map((i) => (
              <IndexerRow
                key={i.id}
                indexer={i}
                definition={definitionOf(i)}
                canEdit={canEdit}
                onOpen={() => setView({ kind: 'details', id: i.id })}
                onEdit={() => setView({ kind: 'form', indexer: i, definition: definitionOf(i) })}
              />
            ))}
          </ul>
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
        />
      )}
      {view.kind === 'details' && (
        <IndexerDetails
          id={view.id}
          fallback={list.find((i) => i.id === view.id) ?? null}
          canEdit={canEdit}
          onClose={close}
          onEdit={(d) => setView({ kind: 'form', indexer: d.indexer, definition: d.definition, capabilities: d.capabilities })}
          onClone={(d) => setView({ kind: 'form', indexer: cloneOf(d.indexer), definition: d.definition, capabilities: d.capabilities })}
        />
      )}
      {view.kind === 'testAll' && <TestAll onClose={close} onOpen={(id) => setView({ kind: 'details', id })} />}
      {view.kind === 'import' && <ProwlarrImport onClose={close} onTestAll={() => setView({ kind: 'testAll' })} />}
    </section>
  )
}

function Meter({ usage }: { usage: Usage }) {
  if (usage.ratio == null) return <span className={styles.usage}>{usage.text}</span>
  const tone = usage.ratio >= 1 ? styles.full : usage.ratio >= 0.8 ? styles.high : ''
  return (
    <span className={`${styles.usage} ${tone}`}>
      {usage.text}
      <span className={styles.meter} aria-hidden="true">
        <span style={{ width: `${Math.round(usage.ratio * 100)}%` }} />
      </span>
    </span>
  )
}

function IndexerRow({
  indexer: i,
  definition,
  canEdit,
  onOpen,
  onEdit,
}: {
  indexer: Indexer
  definition: IndexerDefinition | null
  canEdit: boolean
  onOpen: () => void
  onEdit: () => void
}) {
  const state = rowState(i)
  const status = i.status
  const vip = vipState(status)
  const uses = usesList(i)

  return (
    <li className={`${styles.row} ${styles[state]}`} onClick={onOpen}>
      <div className={styles.main}>
        <div className={styles.nameLine}>
          <button
            type="button"
            className={styles.name}
            title={state === 'enabled' ? 'Enabled' : state === 'disabled' ? 'Disabled' : 'Failing'}
            onClick={(e) => {
              e.stopPropagation()
              onOpen()
            }}
          >
            {i.name}
          </button>
          <ProtocolChip protocol={definition?.protocol ?? 'usenet'} />
          {definition && <PrivacyChip privacy={definition.privacy} />}
        </div>
        <div className={styles.badges}>
          <span className={styles.host}>{hostOf(i.baseUrl)}</span>
          {!i.enable && <Pill tone="off">Disabled</Pill>}
          {i.enable && status && !status.healthy && status.disabledTill && (
            <Pill tone="error">Skipped until {formatUntil(status.disabledTill)}</Pill>
          )}
          {status?.limitReached && <Pill tone="error">{status.limitReached === 'query' ? 'Query limit reached' : 'Grab limit reached'}</Pill>}
          {vip && <Pill tone={vip.tone}>{vip.text}</Pill>}
        </div>
      </div>
      <div className={styles.cell}>
        <span className={styles.cellLabel}>Priority</span>
        <span className={styles.priority}>{i.priority}</span>
      </div>
      <div className={styles.cell}>
        <span className={styles.cellLabel}>Used for</span>
        <span className={styles.uses}>{!i.enable ? '–' : uses.length > 0 ? uses.join(' · ') : 'Nothing'}</span>
      </div>
      <div className={styles.cell}>
        <span className={styles.cellLabel}>Queries</span>
        <Meter usage={queryUsage(i)} />
      </div>
      <div className={styles.cell}>
        <span className={styles.cellLabel}>Grabs</span>
        <Meter usage={grabUsage(i)} />
      </div>
      <div className={styles.actions}>
        {canEdit && (
          <button
            className={ui.button}
            type="button"
            onClick={(e) => {
              e.stopPropagation()
              onEdit()
            }}
          >
            Edit
          </button>
        )}
      </div>
      {status?.lastError && (
        <p className={styles.lastError}>
          {status.healthy ? 'Last error' : 'Error'}: {status.lastError}
        </p>
      )}
    </li>
  )
}
