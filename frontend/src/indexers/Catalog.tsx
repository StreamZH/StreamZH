import { useQuery } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import {
  genericDefinitionKey,
  genericTorznabKey,
  indexerKeys,
  isGenericDefinition,
  indexersApi,
  standardCategoryNames,
  topCategories,
  type Indexer,
  type IndexerDefinition,
  type IndexerPrivacy,
  type IndexerProtocol,
} from '../api/indexers'
import ui from '../components/ui.module.css'
import { CategoryChips, PrivacyChip, ProtocolChip } from './Chips'
import { languageName, privacyLabels, protocolLabels, topCategoryOf } from './labels'
import Dialog from './Dialog'
import MultiSelect from './MultiSelect'
import styles from './Catalog.module.css'

type SortKey = 'protocol' | 'name' | 'language' | 'privacy'

const privacyOrder: Record<IndexerPrivacy, number> = { private: 0, semiPrivate: 1, public: 2 }

function compare(a: IndexerDefinition, b: IndexerDefinition, key: SortKey): number {
  switch (key) {
    case 'protocol':
      return a.protocol.localeCompare(b.protocol)
    case 'language':
      return a.language.localeCompare(b.language)
    case 'privacy':
      return privacyOrder[a.privacy] - privacyOrder[b.privacy]
    default:
      return 0
  }
}

/** "Add indexer": the catalog of known indexers, like Prowlarr's, with search, filters and sorting. */
export default function Catalog({
  existing,
  onSelect,
  onImport,
  onClose,
}: {
  existing: Indexer[]
  onSelect: (definition: IndexerDefinition) => void
  onImport: () => void
  onClose: () => void
}) {
  const definitions = useQuery({ queryKey: indexerKeys.definitions, queryFn: indexersApi.definitions, staleTime: 10 * 60_000 })
  const [search, setSearch] = useState('')
  const [protocols, setProtocols] = useState<IndexerProtocol[]>([])
  const [languages, setLanguages] = useState<string[]>([])
  const [privacy, setPrivacy] = useState<IndexerPrivacy[]>([])
  const [categories, setCategories] = useState<number[]>([])
  const [sort, setSort] = useState<{ key: SortKey; desc: boolean }>({ key: 'name', desc: false })

  const all = useMemo(() => definitions.data ?? [], [definitions.data])
  const added = useMemo(() => new Set(existing.map((i) => i.definitionKey).filter(Boolean)), [existing])

  const languageOptions = useMemo(
    () =>
      Array.from(new Set(all.map((d) => d.language)))
        .sort()
        .map((code) => ({ value: code, label: languageName(code), hint: code })),
    [all],
  )

  const shown = useMemo(() => {
    const term = search.trim().toLowerCase()
    const list = all.filter((d) => {
      if (term && !d.name.toLowerCase().includes(term) && !d.description.toLowerCase().includes(term)) return false
      if (protocols.length > 0 && !protocols.includes(d.protocol)) return false
      if (languages.length > 0 && !languages.includes(d.language)) return false
      if (privacy.length > 0 && !privacy.includes(d.privacy)) return false
      if (categories.length > 0 && !d.categories.some((c) => categories.includes(topCategoryOf(c)))) return false
      return true
    })
    const byName = (a: IndexerDefinition, b: IndexerDefinition) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })
    return list.sort((a, b) => {
      // Generic Newznab and Generic Torznab stay on top: they are the fallback for any indexer not in the list.
      const genericA = isGenericDefinition(a.key)
      const genericB = isGenericDefinition(b.key)
      if (genericA && genericB) return a.key === genericDefinitionKey ? -1 : 1
      if (genericA || genericB) return genericA ? -1 : 1
      // Supported entries before the ones that arrive later (torrent in 1.0).
      if (a.supported !== b.supported) return a.supported ? -1 : 1
      const primary = sort.key === 'name' ? byName(a, b) : compare(a, b, sort.key) || byName(a, b)
      return sort.desc ? -primary : primary
    })
  }, [all, search, protocols, languages, privacy, categories, sort])

  const filtered = search !== '' || protocols.length + languages.length + privacy.length + categories.length > 0
  const generic = all.find((d) => d.key === genericDefinitionKey)
  const torznab = all.find((d) => d.key === genericTorznabKey)

  const resetFilters = () => {
    setSearch('')
    setProtocols([])
    setLanguages([])
    setPrivacy([])
    setCategories([])
  }

  const header = (key: SortKey, label: string) => {
    const active = sort.key === key
    return (
      <th aria-sort={active ? (sort.desc ? 'descending' : 'ascending') : 'none'}>
        <button
          type="button"
          className={`${styles.sort} ${active ? styles.sortActive : ''}`}
          onClick={() => setSort((s) => ({ key, desc: s.key === key ? !s.desc : false }))}
        >
          {label}
          <span className={styles.sortIcon} aria-hidden="true">
            {active ? (sort.desc ? '↓' : '↑') : '↕'}
          </span>
        </button>
      </th>
    )
  }

  const count = shown.filter((d) => d.supported).length

  return (
    <Dialog
      title="Add indexer"
      size="wide"
      tall
      onClose={onClose}
      footer={
        <>
          <span className={styles.count}>
            <strong>{definitions.isSuccess ? count : '–'}</strong> indexer(s) available
            {filtered && definitions.isSuccess && <span className={ui.muted}> · {all.filter((d) => d.supported).length} in total</span>}
          </span>
          <span className={styles.spacer} />
          <button className={ui.button} type="button" onClick={onImport}>
            Import from Prowlarr
          </button>
          <button className={ui.button} type="button" onClick={onClose}>
            Close
          </button>
        </>
      }
    >
      <div className={styles.toolbar}>
        <label className={styles.search}>
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.75" aria-hidden="true">
            <circle cx="11" cy="11" r="7" />
            <path d="m20 20-3.5-3.5" />
          </svg>
          <input
            className={styles.searchInput}
            type="search"
            placeholder="Search indexers"
            aria-label="Search indexers"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            autoFocus
          />
        </label>
        <div className={styles.filters}>
          <MultiSelect
            label="Protocol"
            options={(['usenet', 'torrent'] as const).map((p) => ({ value: p, label: protocolLabels[p] }))}
            selected={protocols}
            onChange={setProtocols}
          />
          <MultiSelect label="Language" options={languageOptions} selected={languages} onChange={setLanguages} />
          <MultiSelect
            label="Privacy"
            options={(['private', 'semiPrivate', 'public'] as const).map((p) => ({ value: p, label: privacyLabels[p] }))}
            selected={privacy}
            onChange={setPrivacy}
          />
          <MultiSelect
            label="Categories"
            options={topCategories.map((c) => ({ value: c, label: standardCategoryNames[c], hint: String(c) }))}
            selected={categories}
            onChange={setCategories}
          />
        </div>
      </div>

      <p className={styles.note}>
        StreamZH supports any Usenet indexer with the Newznab standard and any torrent indexer with a Torznab feed: pick yours below or use{' '}
        {generic ? (
          <button type="button" className={styles.inlineLink} onClick={() => onSelect(generic)}>
            Generic Newznab
          </button>
        ) : (
          'Generic Newznab'
        )}{' '}
        or{' '}
        {torznab ? (
          <button type="button" className={styles.inlineLink} onClick={() => onSelect(torznab)}>
            Generic Torznab
          </button>
        ) : (
          'Generic Torznab'
        )}
        .
      </p>

      {definitions.isPending && <p className={ui.muted}>Loading the indexer list…</p>}
      {definitions.isError && (
        <div className={styles.loadError}>
          <p className={ui.error}>The indexer list could not be loaded: {definitions.error.message}</p>
          <button className={ui.button} type="button" onClick={() => void definitions.refetch()}>
            Try again
          </button>
        </div>
      )}

      {definitions.isSuccess && shown.length === 0 && (
        <div className={styles.empty}>
          <p>No indexer matches these filters.</p>
          <button className={ui.button} type="button" onClick={resetFilters}>
            Clear filters
          </button>
        </div>
      )}

      {shown.length > 0 && (
        <table className={styles.table}>
          <thead>
            <tr>
              {header('protocol', 'Protocol')}
              {header('name', 'Name')}
              {header('language', 'Language')}
              <th>Description</th>
              {header('privacy', 'Privacy')}
              <th>Categories</th>
            </tr>
          </thead>
          <tbody>
            {shown.map((d) => {
              const isAdded = !isGenericDefinition(d.key) && added.has(d.key)
              return (
                <tr
                  key={d.key}
                  className={`${styles.row} ${d.supported ? '' : styles.unsupported}`}
                  onClick={d.supported ? () => onSelect(d) : undefined}
                  aria-disabled={!d.supported || undefined}
                >
                  <td className={styles.protocol} data-label="Protocol">
                    <ProtocolChip protocol={d.protocol} />
                  </td>
                  <td className={styles.name} data-label="Name">
                    {d.supported ? (
                      <button type="button" className={styles.nameButton} onClick={(e) => {
                          e.stopPropagation()
                          onSelect(d)
                        }}>
                        {d.name}
                      </button>
                    ) : (
                      <span className={styles.nameText}>{d.name}</span>
                    )}
                    {isAdded && (
                      <span className={styles.added} title="You already have an indexer from this entry">
                        <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.25" aria-hidden="true">
                          <path d="m5 12.5 4.5 4.5L19 7.5" />
                        </svg>
                        Added
                      </span>
                    )}
                  </td>
                  <td className={styles.language} data-label="Language" title={languageName(d.language)}>
                    {d.language}
                  </td>
                  <td className={styles.description} data-label="Description">
                    <span className={styles.descText}>{d.description}</span>
                    {!d.supported && <span className={styles.reason}>{d.unsupportedReason ?? 'Not supported yet.'}</span>}
                  </td>
                  <td className={styles.privacy} data-label="Privacy">
                    <PrivacyChip privacy={d.privacy} />
                  </td>
                  <td className={styles.categories} data-label="Categories">
                    <CategoryChips ids={d.categories} />
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      )}
    </Dialog>
  )
}
