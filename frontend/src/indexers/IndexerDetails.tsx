import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import {
  genericDefinitionKey,
  indexerKeys,
  indexersApi,
  protocolOf,
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
import {
  formatAgo,
  formatUntil,
  grabUsage,
  hostOf,
  missingApiKey,
  newznabUrl,
  presetAddress,
  queryUsage,
  rowState,
  throughProwlarr,
  usesList,
  vipState,
  withOwnAddress,
} from './model'
import styles from './Details.module.css'

type Tab = 'details' | 'categories'
const tabs: { key: Tab; label: string }[] = [
  { key: 'details', label: 'Details' },
  { key: 'categories', label: 'Categories' },
]

/** Indexer info like Prowlarr's: what the indexer is, how StreamZH talks to it and what it can search, with a Test. */
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
  /** Opens Edit; `indexer` replaces the saved one as the form's starting point (e.g. moved to its own address). */
  onEdit: (details: Details, indexer?: Details['indexer']) => void
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
  // Tests the saved indexer with its stored key; a success stores the capabilities and ends a waiting time.
  const test = useMutation({
    mutationFn: (indexer: Details['indexer']) => indexersApi.test(indexer),
    onSettled: () => queryClient.invalidateQueries({ queryKey: indexerKeys.all }),
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
      <ProtocolChip protocol={protocolOf(indexer)} />
      {definition && <PrivacyChip privacy={definition.privacy} />}
    </>
  )

  const testLine = (test.isPending || test.isSuccess || test.isError) && (
    <p className={styles.testResult} aria-live="polite">
      {test.isPending && <span className={ui.muted}>Testing the connection…</span>}
      {test.isSuccess && (
        <span className={ui.ok}>
          Connection works{test.data.categories.length > 0 ? `, ${test.data.categories.length} category groups loaded` : ''}.
        </span>
      )}
      {test.isError && <span className={ui.error}>{test.error.message}</span>}
    </p>
  )

  const footer = confirmDelete ? (
    <>
      <span className={styles.confirm}>Delete {indexer?.name}?</span>
      <span className={styles.spacer} />
      <button className={ui.button} type="button" onClick={() => setConfirmDelete(false)}>
        Keep
      </button>
      <button className={ui.dangerSolid} type="button" onClick={() => remove.mutate()} disabled={remove.isPending}>
        {remove.isPending ? 'Deleting…' : 'Yes, delete'}
      </button>
    </>
  ) : (
    <>
      {remove.error && <p className={`${ui.error} ${styles.footerError}`}>{remove.error.message}</p>}
      {testLine}
      {canEdit && (
        <>
          <button className={ui.danger} type="button" onClick={() => setConfirmDelete(true)}>
            Delete
          </button>
          <button className={ui.button} type="button" disabled={!details.data} onClick={() => details.data && onClone(details.data)}>
            Clone
          </button>
        </>
      )}
      <span className={styles.spacer} />
      {canEdit && (
        <>
          <button
            className={ui.button}
            type="button"
            disabled={!details.data || test.isPending}
            onClick={() => details.data && test.mutate(details.data.indexer)}
            title={details.data && missingApiKey(details.data.indexer) ? 'No API key saved yet: the test will probably fail' : undefined}
          >
            {test.isPending ? 'Testing…' : 'Test'}
          </button>
          <button className={ui.button} type="button" disabled={!details.data} onClick={() => details.data && onEdit(details.data)}>
            Edit
          </button>
        </>
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
        {details.data && tab === 'details' && (
          <DetailsTab details={details.data} canEdit={canEdit} onUseOwnAddress={(moved) => details.data && onEdit(details.data, moved)} />
        )}
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

/** "Ratio 1.5 or 3 days, season packs 7 days" / "Download client default". */
function seedGoals(i: Details['indexer']): string {
  const days = (minutes: number) => (minutes % 1440 === 0 ? `${minutes / 1440} day${minutes === 1440 ? '' : 's'}` : minutes % 60 === 0 ? `${minutes / 60} h` : `${minutes} min`)
  const parts = [i.seedRatio != null && i.seedRatio > 0 ? `ratio ${i.seedRatio}` : null, i.seedTimeMinutes ? days(i.seedTimeMinutes) : null].filter(Boolean)
  const pack = i.seasonPackSeedTimeMinutes ? `, season packs ${days(i.seasonPackSeedTimeMinutes)}` : ''
  if (parts.length === 0 && !pack) return 'Download client default'
  const text = parts.length > 0 ? parts.join(' or ') : 'client default'
  return text.charAt(0).toUpperCase() + text.slice(1) + pack
}

function DetailsTab({
  details,
  canEdit,
  onUseOwnAddress,
}: {
  details: Details
  canEdit: boolean
  onUseOwnAddress: (indexer: Details['indexer']) => void
}) {
  const { indexer, definition, capabilities, capabilitiesCheckedAt } = details
  const status = indexer.status
  const vip = vipState(status)
  const site = definition?.siteUrl ?? (indexer.baseUrl || null)
  const uses = usesList(indexer)
  const queries = queryUsage(indexer)
  const grabs = grabUsage(indexer)
  const viaProwlarr = throughProwlarr(indexer)
  const own = presetAddress(definition)
  const moved = withOwnAddress(indexer, definition)

  return (
    <div className={styles.sections}>
      {status && !status.healthy && status.disabledTill && (
        <p className={styles.statusNote}>
          Skipped until {formatUntil(status.disabledTill)}
          {status.lastError ? `: ${status.lastError}` : ''}
        </p>
      )}

      {viaProwlarr && (
        <div className={styles.warnNote} role="note">
          <span>
            Goes through Prowlarr at <span className={styles.mono}>{hostOf(indexer.baseUrl)}</span> instead of the indexer&apos;s own address.
            {own
              ? ` StreamZH can reach ${hostOf(own.baseUrl)} directly; you only need its API key.`
              : ' If the indexer has an API of its own, enter its address and API key under Edit. Site scrapers without an API can only be searched through Prowlarr, which then has to keep running.'}
          </span>
          {canEdit && moved && (
            <button className={`${ui.button} ${ui.sm}`} type="button" onClick={() => onUseOwnAddress(moved)}>
              Use {hostOf(own!.baseUrl)}
            </button>
          )}
        </div>
      )}

      {!viaProwlarr && canEdit && missingApiKey(indexer) && (
        <p className={styles.warnNote} role="note">
          <span>No API key saved. Most indexers need one: enter it under Edit{indexer.enable ? '' : ', then switch the indexer on'}.</span>
        </p>
      )}

      <section>
        <h3 className={styles.sectionTitle}>Indexer</h3>
        <dl className={styles.list}>
          <Item label="Description">{definition?.description || (definition?.key === genericDefinitionKey ? 'Generic Newznab indexer' : protocolOf(indexer) === 'torrent' ? 'Generic Torznab indexer' : '–')}</Item>
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
          <Item label={protocolOf(indexer) === 'torrent' ? 'Torznab URL' : 'Newznab URL'} mono>
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
          {protocolOf(indexer) === 'torrent' && (
            <>
              <Item label="Minimum seeders">{indexer.minimumSeeders ?? 1}</Item>
              <Item label="Seed goals">{seedGoals(indexer)}</Item>
            </>
          )}
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
  return <p className={styles.empty}>Not checked yet. Press Test to read what this indexer can search.</p>
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
