import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import {
  genericDefinitionKey,
  indexerKeys,
  indexersApi,
  type Indexer,
  type IndexerCapabilities,
  type IndexerCategory,
  type IndexerDetails as Details,
} from '../api/indexers'
import ui from '../components/ui.module.css'
import { formatDate, formatDateTime } from '../lib/format'
import { Pill, PrivacyChip, ProtocolChip } from './Chips'
import { languageName } from './labels'
import Dialog from './Dialog'
import { formatAgo, formatUntil, grabUsage, newznabUrl, queryUsage, rowState, usesList, vipState } from './model'
import styles from './Details.module.css'

type Tab = 'details' | 'categories'
const tabs: { key: Tab; label: string }[] = [
  { key: 'details', label: 'Details' },
  { key: 'categories', label: 'Categories' },
]

/** Indexer info like Prowlarr's: what the indexer is, how StreamZH talks to it and what it can search. */
export default function IndexerDetails({
  id,
  fallback,
  canEdit,
  onEdit,
  onClone,
  onClose,
}: {
  id: number
  /** List row shown while the details load. */
  fallback: Indexer | null
  canEdit: boolean
  onEdit: (details: Details) => void
  onClone: (details: Details) => void
  onClose: () => void
}) {
  const queryClient = useQueryClient()
  const details = useQuery({ queryKey: indexerKeys.details(id), queryFn: () => indexersApi.details(id) })
  const [tab, setTab] = useState<Tab>('details')
  const [confirmDelete, setConfirmDelete] = useState(false)
  const tabRefs = useRef<(HTMLButtonElement | null)[]>([])

  const remove = useMutation({
    mutationFn: () => indexersApi.remove(id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: indexerKeys.all })
      onClose()
    },
  })

  const indexer = details.data?.indexer ?? fallback
  const definition = details.data?.definition ?? null

  const onTabKey = (e: KeyboardEvent, index: number) => {
    if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return
    e.preventDefault()
    const next = (index + (e.key === 'ArrowRight' ? 1 : tabs.length - 1)) % tabs.length
    setTab(tabs[next].key)
    tabRefs.current[next]?.focus()
  }

  const state = indexer ? rowState(indexer) : null
  const subtitle = indexer && (
    <>
      {state === 'disabled' && <Pill tone="off">Disabled</Pill>}
      {state === 'error' && <Pill tone="error">{indexer.status?.limitReached ? 'Limit reached' : 'Failing'}</Pill>}
      {state === 'enabled' && <Pill tone="ok">Enabled</Pill>}
      <ProtocolChip protocol={definition?.protocol ?? 'usenet'} />
      {definition && <PrivacyChip privacy={definition.privacy} />}
    </>
  )

  const footer = confirmDelete ? (
    <>
      <span className={styles.confirm}>Delete {indexer?.name}?</span>
      <span className={styles.spacer} />
      <button className={ui.button} type="button" onClick={() => setConfirmDelete(false)}>
        Keep
      </button>
      <button className={ui.danger} type="button" onClick={() => remove.mutate()} disabled={remove.isPending}>
        {remove.isPending ? 'Deleting…' : 'Yes, delete'}
      </button>
    </>
  ) : (
    <>
      {remove.error && <p className={`${ui.error} ${styles.footerError}`}>{remove.error.message}</p>}
      {canEdit && (
        <>
          <button className={`${ui.button} ${styles.delete}`} type="button" onClick={() => setConfirmDelete(true)}>
            Delete
          </button>
          <button className={ui.button} type="button" disabled={!details.data} onClick={() => details.data && onClone(details.data)}>
            Clone
          </button>
        </>
      )}
      <span className={styles.spacer} />
      {canEdit && (
        <button className={ui.button} type="button" disabled={!details.data} onClick={() => details.data && onEdit(details.data)}>
          Edit
        </button>
      )}
      <button className={canEdit ? ui.button : ui.primary} type="button" onClick={onClose}>
        Close
      </button>
    </>
  )

  return (
    <Dialog title={indexer?.name ?? 'Indexer'} subtitle={subtitle} onClose={onClose} footer={footer}>
      <div className={styles.tabs} role="tablist" aria-label="Indexer information">
        {tabs.map((t, index) => (
          <button
            key={t.key}
            ref={(el) => {
              tabRefs.current[index] = el
            }}
            type="button"
            role="tab"
            id={`indexer-tab-${t.key}`}
            aria-selected={tab === t.key}
            aria-controls={`indexer-panel-${t.key}`}
            tabIndex={tab === t.key ? 0 : -1}
            className={`${styles.tab} ${tab === t.key ? styles.tabActive : ''}`}
            onClick={() => setTab(t.key)}
            onKeyDown={(e) => onTabKey(e, index)}
          >
            {t.label}
          </button>
        ))}
      </div>

      <div role="tabpanel" id={`indexer-panel-${tab}`} aria-labelledby={`indexer-tab-${tab}`} className={styles.panel}>
        {details.isPending && <p className={ui.muted}>Loading…</p>}
        {details.isError && <p className={ui.error}>{details.error.message}</p>}
        {details.data && tab === 'details' && <DetailsTab details={details.data} />}
        {details.data && tab === 'categories' && <CategoriesTab details={details.data} />}
      </div>
    </Dialog>
  )
}

function Item({ label, children, mono }: { label: string; children: ReactNode; mono?: boolean }) {
  return (
    <div className={styles.item}>
      <dt>{label}</dt>
      <dd className={mono ? styles.mono : undefined}>{children}</dd>
    </div>
  )
}

function Params({ params }: { params: string[] }) {
  if (params.length === 0) return <span className={styles.no}>Not supported</span>
  return (
    <span className={styles.params}>
      {params.map((p) => (
        <span key={p} className={styles.param}>
          {p}
        </span>
      ))}
    </span>
  )
}

function DetailsTab({ details }: { details: Details }) {
  const { indexer, definition, capabilities, capabilitiesCheckedAt } = details
  const status = indexer.status
  const vip = vipState(status)
  const site = definition?.siteUrl ?? (indexer.baseUrl || null)
  const uses = usesList(indexer)
  const queries = queryUsage(indexer)
  const grabs = grabUsage(indexer)

  return (
    <div className={styles.sections}>
      {status && !status.healthy && status.disabledTill && (
        <p className={styles.statusNote}>
          Skipped until {formatUntil(status.disabledTill)}
          {status.lastError ? `: ${status.lastError}` : ''}
        </p>
      )}

      <section>
        <h3 className={styles.sectionTitle}>Indexer</h3>
        <dl className={styles.list}>
          <Item label="Description">{definition?.description || (definition?.key === genericDefinitionKey ? 'Generic Newznab indexer' : '–')}</Item>
          <Item label="Language">{definition ? `${languageName(definition.language)} (${definition.language})` : '–'}</Item>
          <Item label="Privacy">{definition ? <PrivacyChip privacy={definition.privacy} /> : '–'}</Item>
          <Item label="Indexer site">
            {site ? (
              <a className={styles.link} href={site} target="_blank" rel="noreferrer noopener">
                {site}
              </a>
            ) : (
              '–'
            )}
          </Item>
          <Item label="Newznab URL" mono>
            {indexer.baseUrl ? newznabUrl(indexer) : '–'}
          </Item>
          {indexer.additionalParameters && (
            <Item label="Additional parameters" mono>
              {indexer.additionalParameters}
            </Item>
          )}
        </dl>
      </section>

      <section>
        <h3 className={styles.sectionTitle}>In StreamZH</h3>
        <dl className={styles.list}>
          <Item label="Priority">{indexer.priority}</Item>
          <Item label="Used for">{indexer.enable ? (uses.length > 0 ? uses.join(' · ') : 'Nothing') : 'Disabled'}</Item>
          <Item label="Queries">{queries.text}</Item>
          <Item label="Grabs">{grabs.text}</Item>
          {indexer.vipExpiration && (
            <Item label="VIP expiration">
              {formatDate(indexer.vipExpiration)}
              {vip && (
                <>
                  {' '}
                  <Pill tone={vip.tone}>{vip.text}</Pill>
                </>
              )}
            </Item>
          )}
          {status?.lastError && <Item label={status.healthy ? 'Last error' : 'Error'}>{status.lastError}</Item>}
        </dl>
      </section>

      <section>
        <h3 className={styles.sectionTitle}>Search capabilities</h3>
        {capabilities ? (
          <dl className={styles.list}>
            <Item label="Raw search">{capabilities.searchAvailable ? 'Supported' : <span className={styles.no}>Not supported</span>}</Item>
            <Item label="Search">
              <Params params={capabilities.searchAvailable ? ['q'] : []} />
            </Item>
            <Item label="TV search">
              <Params params={capabilities.tvSearchAvailable ? capabilities.tvSearchParams : []} />
            </Item>
            <Item label="Movie search">
              <Params params={capabilities.movieSearchAvailable ? capabilities.movieSearchParams : []} />
            </Item>
            <Item label="Page size" mono>
              {capabilities.defaultPageSize} / max {capabilities.maxPageSize}
            </Item>
            <Item label="Last checked">{capabilitiesCheckedAt ? <span title={formatDateTime(capabilitiesCheckedAt)}>{formatAgo(capabilitiesCheckedAt)}</span> : '–'}</Item>
          </dl>
        ) : (
          <NotChecked />
        )}
      </section>
    </div>
  )
}

function NotChecked() {
  return <p className={styles.empty}>Not checked yet. Open Edit and press Test to read what this indexer can search.</p>
}

function CategoriesTab({ details }: { details: Details }) {
  const caps: IndexerCapabilities | null = details.capabilities
  if (!caps || caps.categories.length === 0) return <NotChecked />
  const used = new Set([...details.indexer.categories, ...details.indexer.tvCategories])
  const sorted = (list: IndexerCategory[]) => [...list].sort((a, b) => a.id - b.id)

  return (
    <div className={styles.tree}>
      <p className={styles.treeHint}>
        <span className={styles.usedDot} aria-hidden="true" /> used by StreamZH for movie and TV searches
      </p>
      <table className={styles.catTable}>
        <thead>
          <tr>
            <th>ID</th>
            <th>Name</th>
          </tr>
        </thead>
        {sorted(caps.categories).map((parent) => (
          <tbody key={parent.id}>
            <CategoryRow category={parent} used={used.has(parent.id)} />
            {sorted(parent.subCategories).map((sub) => (
              <CategoryRow key={sub.id} category={sub} used={used.has(sub.id)} sub />
            ))}
          </tbody>
        ))}
      </table>
    </div>
  )
}

function CategoryRow({ category, used, sub }: { category: IndexerCategory; used: boolean; sub?: boolean }) {
  return (
    <tr className={sub ? styles.subRow : styles.parentRow}>
      <td className={styles.catIdCell}>{category.id}</td>
      <td>
        <span className={sub ? styles.subName : undefined}>{category.name}</span>
        {used && <span className={styles.usedDot} title="Used by StreamZH" />}
      </td>
    </tr>
  )
}
