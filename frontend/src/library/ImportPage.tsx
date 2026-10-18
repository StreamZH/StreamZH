import { useMutation, useQuery } from '@tanstack/react-query'
import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { Link, useSearchParams } from 'react-router'
import { libraryApi, queryKeys, type ImportCandidate, type LookupResult, type MediaType } from '../api/library'
import Icon from '../components/Icon'
import Loader from '../components/Loader'
import PageHeader from '../components/PageHeader'
import ProfileSelect from '../components/ProfileSelect'
import ui from '../components/ui.module.css'
import { formatBytes } from '../lib/format'
import page from '../pages/Page.module.css'
import styles from './Import.module.css'

interface Match {
  state: 'searching' | 'done' | 'error'
  matches: LookupResult[]
  tmdbId: number | null
  include: boolean
}

interface Row extends Match {
  candidate: ImportCandidate
}

const pending: Match = { state: 'searching', matches: [], tmdbId: null, include: true }

/** Adds existing folders in a root folder to the library, without moving or renaming files. */
export default function ImportPage() {
  const [params] = useSearchParams()
  const type = (params.get('type') === 'Series' ? 'Series' : 'Movie') as MediaType
  const rootFolders = useQuery({ queryKey: queryKeys.rootFolders, queryFn: libraryApi.rootFolders })
  const folders = useMemo(() => (rootFolders.data ?? []).filter((r) => r.mediaType === type), [rootFolders.data, type])
  const [rootFolderId, setRootFolderId] = useState<number | null>(null)
  const active = rootFolderId ?? folders.find((f) => f.unmappedFolders > 0)?.id ?? folders[0]?.id ?? null

  return (
    <div className={page.page}>
      <PageHeader
        eyebrow={
          <Link className={styles.back} to={type === 'Movie' ? '/library/movies' : '/library/series'}>
            <Icon name="left" size={13} />
            {type === 'Movie' ? 'Movies' : 'Series'}
          </Link>
        }
        title={`Import existing ${type === 'Movie' ? 'movies' : 'series'}`}
        lead="StreamZH matches each folder with TMDB and adds it to the library. Nothing is moved or renamed."
      />

      {folders.length > 0 && (
        <div className={styles.toolbar}>
          <label className={styles.rootPick}>
            <Icon name="folder" size={15} />
            <select className={ui.select} value={active ?? ''} onChange={(e) => setRootFolderId(Number(e.target.value))} aria-label="Root folder">
              {folders.map((f) => (
                <option key={f.id} value={f.id}>
                  {f.path} ({f.unmappedFolders} new)
                </option>
              ))}
            </select>
          </label>
        </div>
      )}

      {rootFolders.data && folders.length === 0 && (
        <div className={`${ui.panel} ${ui.empty}`}>
          <Icon name="folderplus" size={22} />
          <h3>No root folder yet</h3>
          <span>
            Add a root folder for {type === 'Movie' ? 'movies' : 'series'} <Link to="/settings/media-management">in settings</Link>.
          </span>
        </div>
      )}

      {active && <ImportTable key={active} rootFolderId={active} type={type} />}
    </div>
  )
}

function ImportTable({ rootFolderId, type }: { rootFolderId: number; type: MediaType }) {
  const candidates = useQuery({
    queryKey: ['import-candidates', rootFolderId],
    queryFn: () => libraryApi.importCandidates(rootFolderId),
    staleTime: Infinity,
  })
  const [results, setResults] = useState<Record<string, Match>>({})
  const [monitored, setMonitored] = useState(true)
  const [qualityProfileId, setQualityProfileId] = useState<number | null>(null)
  const jobs = useQuery({ queryKey: queryKeys.jobs, queryFn: libraryApi.jobs, refetchInterval: 4000 })

  const rows = useMemo<Row[]>(
    () => (candidates.data ?? []).map((c) => ({ candidate: c, ...(results[c.folderName] ?? pending) })),
    [candidates.data, results],
  )

  // Look up matches on TMDB, three folders at a time.
  useEffect(() => {
    if (!candidates.data) return
    let cancelled = false
    const queue = [...candidates.data]
    const store = (folder: string, match: Match) => {
      if (!cancelled) setResults((prev) => ({ ...prev, [folder]: match }))
    }

    const worker = async () => {
      while (!cancelled && queue.length > 0) {
        const c = queue.shift()!
        try {
          const all = await searchWithFallbacks(type, c.title, c.year)
          const pick = pickMatch(all, c)
          store(c.folderName, { state: 'done', matches: all, tmdbId: pick?.tmdbId ?? null, include: !!pick && !pick.libraryId })
        } catch {
          store(c.folderName, { state: 'error', matches: [], tmdbId: null, include: false })
        }
      }
    }
    void Promise.all([worker(), worker(), worker()])
    return () => {
      cancelled = true
    }
  }, [candidates.data, type])

  const selected = rows.filter((r) => r.include && r.tmdbId)
  const searching = rows.filter((r) => r.state === 'searching').length

  const importFolders = useMutation({
    mutationFn: () =>
      libraryApi.importFolders(
        rootFolderId,
        monitored,
        qualityProfileId,
        selected.map((r) => ({ folderName: r.candidate.folderName, tmdbId: r.tmdbId! })),
      ),
  })

  const update = (row: Row, change: Partial<Match>) => {
    const { candidate, ...match } = row
    setResults((prev) => ({ ...prev, [candidate.folderName]: { ...match, ...change } }))
  }

  if (candidates.isError) return <p className={ui.error}>{candidates.error.message}</p>
  if (!candidates.data) return <Loader text="Reading folders…" />
  if (candidates.data.length === 0)
    return (
      <div className={`${ui.panel} ${ui.empty}`}>
        <Icon name="check" size={22} />
        <h3>Nothing to import</h3>
        <span>Every folder in this root folder is already in the library.</span>
      </div>
    )

  if (importFolders.isSuccess) {
    const busy = (jobs.data?.queued ?? 0) > 0 || jobs.data?.running
    // Rough share done: the queue left against the folders handed over (other jobs can be in the queue too).
    const share = Math.min(1, Math.max(0, 1 - (jobs.data?.queued ?? 0) / Math.max(1, importFolders.data.queued)))
    return (
      <div className={`${ui.panel} ${styles.done}`}>
        <div className={ui.panelHead}>
          <h2 className={styles.doneTitle}>
            {busy ? <span className={ui.dotBusy} /> : <span className={ui.dotOk} />}
            {busy ? 'Importing…' : 'Import finished'}
          </h2>
        </div>
        {busy && (
          <div className={styles.track}>
            <i style={{ width: `${Math.max(3, share * 100)}%` }} />
          </div>
        )}
        <p className={`${ui.muted} ${ui.num}`}>
          {busy
            ? `${Math.floor(share * 100)} % · ${jobs.data?.queued ?? 0} left in the queue${jobs.data?.running ? ` · ${jobs.data.running}` : ''}`
            : `${importFolders.data.queued} folders were added.`}
        </p>
        <Link className={busy ? ui.button : ui.primary} to={type === 'Movie' ? '/library/movies' : '/library/series'}>
          Go to library
          <Icon name="right" size={14} />
        </Link>
      </div>
    )
  }

  return (
    <>
      <div className={styles.summary}>
        <span className={`${styles.count} ${ui.num}`}>
          <strong>{rows.length}</strong> folders ·{' '}
          {searching > 0 ? (
            <span className={styles.matching}>
              <span className={ui.dotBusy} />
              matching {rows.length - searching} / {rows.length}…
            </span>
          ) : (
            `${selected.length} ready to import`
          )}
        </span>
        <ProfileSelect className={`${ui.select} ${styles.compact}`} value={qualityProfileId} onChange={setQualityProfileId} />
        <label className={styles.switchRow}>
          <span className={ui.switch}>
            <input type="checkbox" checked={monitored} onChange={(e) => setMonitored(e.target.checked)} />
            <span />
          </span>
          Monitored
        </label>
        <button className={ui.primary} type="button" disabled={selected.length === 0 || importFolders.isPending} onClick={() => importFolders.mutate()}>
          <Icon name="inbox" size={15} />
          Import {selected.length} {selected.length === 1 ? 'folder' : 'folders'}
        </button>
      </div>
      {importFolders.error && <p className={ui.error}>{importFolders.error.message}</p>}

      <div className={styles.tableWrap}>
        <table className={`${ui.table} ${styles.table}`}>
          <thead>
            <tr>
              <th>
                <input
                  type="checkbox"
                  aria-label="Select all"
                  checked={rows.some((r) => r.tmdbId) && rows.every((r) => r.include || !r.tmdbId)}
                  onChange={(e) =>
                    setResults(Object.fromEntries(rows.map(({ candidate, ...m }) => [candidate.folderName, { ...m, include: e.target.checked && !!m.tmdbId }])))
                  }
                />
              </th>
              <th>Folder</th>
              <th>Match on TMDB</th>
              <th className={styles.right}>Files</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => (
              <tr key={row.candidate.folderName} className={row.include ? undefined : styles.skipped}>
                <td>
                  <input
                    type="checkbox"
                    aria-label={`Import ${row.candidate.folderName}`}
                    checked={row.include}
                    disabled={!row.tmdbId}
                    onChange={(e) => update(row, { include: e.target.checked })}
                  />
                </td>
                <td className={styles.folder}>{row.candidate.folderName}</td>
                <td className={styles.match}>
                  <MatchCell row={row} type={type} onChange={(change) => update(row, change)} />
                </td>
                <td className={`${styles.right} ${styles.files}`}>
                  {row.candidate.videoFiles > 0 ? (
                    <>
                      {row.candidate.videoFiles} · {formatBytes(row.candidate.size)}
                    </>
                  ) : (
                    <span className={ui.muted}>empty</span>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  )
}

/**
 * Searches TMDB with the parsed title and year; if nothing is found, without the year and then with
 * German umlauts restored (scene names write "traegt" for "trägt").
 */
async function searchWithFallbacks(type: MediaType, title: string, year: number | null): Promise<LookupResult[]> {
  const attempts: [string, number | null][] = [[title, year]]
  if (year) attempts.push([title, null])
  const umlauts = withUmlauts(title)
  if (umlauts !== title) attempts.push([umlauts, year], [umlauts, null])

  for (const [term, y] of attempts) {
    const found = await libraryApi.lookup(type, term, y)
    if (found.length > 0) return found
  }
  return []
}

function withUmlauts(text: string): string {
  return text
    .replace(/ae/g, 'ä')
    .replace(/oe/g, 'ö')
    .replace(/ue/g, 'ü')
    .replace(/Ae/g, 'Ä')
    .replace(/Oe/g, 'Ö')
    .replace(/Ue/g, 'Ü')
}

/** Exact TMDB id from the folder name wins, then same year, then the first result. */
function pickMatch(results: LookupResult[], c: ImportCandidate): LookupResult | undefined {
  if (c.tmdbId) {
    const exact = results.find((r) => r.tmdbId === c.tmdbId)
    if (exact) return exact
  }
  if (c.year) {
    const sameYear = results.find((r) => r.year === c.year)
    if (sameYear) return sameYear
  }
  return results[0]
}

/** Shows the chosen TMDB match and lets the user search by hand when the automatic match is wrong or missing. */
function MatchCell({ row, type, onChange }: { row: Row; type: MediaType; onChange: (change: Partial<Match>) => void }) {
  const [searching, setSearching] = useState(false)
  const [term, setTerm] = useState(row.candidate.title)
  const [busy, setBusy] = useState(false)
  const [failed, setFailed] = useState(false)

  const search = async (event: FormEvent) => {
    event.preventDefault()
    if (!term.trim()) return
    setBusy(true)
    setFailed(false)
    try {
      const found = await libraryApi.lookup(type, term.trim())
      const pick = found.find((m) => m.libraryId === null)
      onChange({ state: 'done', matches: found, tmdbId: pick?.tmdbId ?? null, include: !!pick })
      setSearching(false)
    } catch {
      setFailed(true)
    } finally {
      setBusy(false)
    }
  }

  if (searching) {
    return (
      <form className={styles.searchRow} onSubmit={search}>
        <input className={`${ui.input} ${styles.compact}`} value={term} onChange={(e) => setTerm(e.target.value)} autoFocus aria-label="Search TMDB" />
        <button className={`${ui.button} ${ui.sm}`} type="submit" disabled={busy}>
          {busy ? '…' : 'Search'}
        </button>
        <button className={`${ui.ghost} ${ui.sm}`} type="button" onClick={() => setSearching(false)}>
          Cancel
        </button>
        {failed && <span className={ui.error}>Search failed</span>}
      </form>
    )
  }

  return (
    <div className={styles.matchRow}>
      {row.state === 'searching' && <span className="sk" style={{ width: '60%' }} />}
      {row.state === 'error' && <span className={ui.error}>Search failed</span>}
      {row.state === 'done' && row.matches.length === 0 && <span className={ui.muted}>No match for “{row.candidate.title}”</span>}
      {row.state === 'done' && row.matches.length > 0 && (
        <select
          className={`${ui.select} ${styles.compact}`}
          value={row.tmdbId ?? ''}
          onChange={(e) => onChange({ tmdbId: e.target.value ? Number(e.target.value) : null, include: !!e.target.value })}
        >
          <option value="">Skip this folder</option>
          {row.matches.map((m) => (
            <option key={m.tmdbId} value={m.tmdbId} disabled={m.libraryId !== null}>
              {m.title} {m.year > 0 ? `(${m.year})` : ''}
              {m.libraryId !== null ? ' · already in library' : ''}
            </option>
          ))}
        </select>
      )}
      {row.state !== 'searching' && (
        <button className={`${ui.ghost} ${ui.sm} ${ui.icon}`} type="button" onClick={() => setSearching(true)} title="Search TMDB by hand" aria-label="Search TMDB by hand">
          <Icon name="search" size={15} />
        </button>
      )}
    </div>
  )
}
