import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router'
import { generalApi, generalKeys, type LogEntry } from '../api/general'
import Icon from '../components/Icon'
import Loader from '../components/Loader'
import ui from '../components/ui.module.css'
import { formatBytes, formatDateTime } from '../lib/format'
import { Section, Toggle } from '../settings/SettingsKit'
import styles from './System.module.css'
import { SystemPage } from './SystemPage'

const levels = [
  { value: '', label: 'All', title: 'All levels' },
  { value: 'Debug', label: 'Debug', title: 'Debug and above' },
  { value: 'Info', label: 'Info', title: 'Info and above' },
  { value: 'Warn', label: 'Warn', title: 'Warnings and errors' },
  { value: 'Error', label: 'Error', title: 'Errors only' },
]

const counts = [100, 300, 1000, 5000]

const entryKey = (e: LogEntry) => `${e.time}|${e.level}|${e.logger}|${e.message}`

/** System → Logs (CloudZH .logbox): the newest entries with level, search and live updates, and the log files. */
export default function LogsPage() {
  return (
    <SystemPage
      title="Logs"
      wide
      lead={
        <>
          The StreamZH log, newest first. <Link to="/settings/general#logging">Settings → General</Link> sets how much is written.
        </>
      }
    >
      <EntriesSection />
      <FilesSection />
    </SystemPage>
  )
}

function EntriesSection() {
  const [level, setLevel] = useState('')
  const [typed, setTyped] = useState('')
  const [search, setSearch] = useState('')
  const [lines, setLines] = useState(300)
  const [live, setLive] = useState(true)

  // Search once typing pauses.
  useEffect(() => {
    const timer = window.setTimeout(() => setSearch(typed.trim()), 350)
    return () => window.clearTimeout(timer)
  }, [typed])

  const logs = useQuery({
    queryKey: [...generalKeys.logs(level, search), lines],
    queryFn: () => generalApi.logs(lines, level, search),
    refetchInterval: live ? 10_000 : false,
    placeholderData: keepPreviousData,
  })
  const entries = useMemo(() => logs.data?.entries ?? [], [logs.data])

  // Entries that arrived with the last refresh flash in (CloudZH .ll.new); a new filter starts over.
  const filter = `${level}|${search}|${lines}`
  const [seen, setSeen] = useState<{ data: unknown; filter: string; keys: Set<string> } | null>(null)
  const [fresh, setFresh] = useState<Set<string>>(() => new Set())
  if (logs.data && logs.data !== seen?.data) {
    const keys = new Set(entries.map(entryKey))
    setFresh(seen && seen.filter === filter ? new Set([...keys].filter((k) => !seen.keys.has(k))) : new Set())
    setSeen({ data: logs.data, filter, keys })
  }

  return (
    <Section id="entries" title="Entries" description="From the current log file. Older entries are in the files below.">
      <div className={styles.logBar}>
        <div className={ui.seg} role="group" aria-label="Level">
          {levels.map((l) => (
            <button key={l.value} type="button" title={l.title} aria-pressed={level === l.value} onClick={() => setLevel(l.value)}>
              {l.label}
            </button>
          ))}
        </div>
        <label className={styles.searchBox}>
          <Icon name="search" size={15} />
          <input className={ui.input} type="search" placeholder="Search message or logger" value={typed} onChange={(e) => setTyped(e.target.value)} aria-label="Search" />
        </label>
        <select className={ui.select} value={lines} onChange={(e) => setLines(Number(e.target.value))} aria-label="Number of entries">
          {counts.map((c) => (
            <option key={c} value={c}>
              Last {c}
            </option>
          ))}
        </select>
        <span className={styles.live}>
          {live && <span className={styles.liveDot} aria-hidden="true" />}
          <Toggle checked={live} onChange={setLive} on="Live" off="Paused" label="Live updates" />
        </span>
        <button
          className={`${ui.button} ${ui.icon}`}
          type="button"
          onClick={() => void logs.refetch()}
          disabled={logs.isFetching}
          aria-label="Refresh"
          title="Refresh"
        >
          <Icon name="restart" size={16} className={logs.isFetching ? styles.spinning : undefined} />
        </button>
      </div>
      {logs.isError && <p className={ui.error}>{logs.error.message}</p>}
      <div className={styles.log} role="log" aria-label="Log entries">
        {logs.isPending && <Loader text="Reading the log…" />}
        {entries.length === 0 && !logs.isPending && <div className={styles.logEmpty}>{level || search ? 'No entries match.' : 'No entries yet.'}</div>}
        {entries.map((entry, index) => (
          <EntryRow key={`${entryKey(entry)}-${index}`} entry={entry} fresh={fresh.has(entryKey(entry))} />
        ))}
      </div>
      <p className={styles.logCount}>
        {entries.length > 0 && (
          <span className={ui.num}>
            {entries.length.toLocaleString()} entr{entries.length === 1 ? 'y' : 'ies'}
          </span>
        )}
      </p>
    </Section>
  )
}

function EntryRow({ entry, fresh }: { entry: LogEntry; fresh: boolean }) {
  const [open, setOpen] = useState(false)
  const levelClass = styles[`level${entry.level}`] ?? ''
  return (
    <div className={`${styles.entry} ${fresh ? styles.entryNew : ''}`}>
      <span className={styles.entryTime}>{entry.time}</span>
      <span className={`${styles.entryLevel} ${levelClass}`}>{entry.level}</span>
      <span className={styles.entryLogger} title={entry.logger}>
        {entry.logger}
      </span>
      <span className={`${styles.entryMessage} ${entry.level === 'Warn' || entry.level === 'Error' || entry.level === 'Fatal' ? levelClass : ''}`}>
        {entry.message}
        {entry.details && (
          <button type="button" className={styles.detailsToggle} onClick={() => setOpen((o) => !o)} aria-expanded={open}>
            {open ? 'Hide details' : 'Details'}
          </button>
        )}
      </span>
      {open && entry.details && <pre className={styles.entryDetails}>{entry.details}</pre>}
    </div>
  )
}

function FilesSection() {
  const queryClient = useQueryClient()
  const files = useQuery({ queryKey: generalKeys.logFiles, queryFn: generalApi.logFiles })
  const [confirm, setConfirm] = useState<string | null>(null)
  const remove = useMutation({
    mutationFn: generalApi.deleteLogFile,
    onSuccess: () => {
      setConfirm(null)
      void queryClient.invalidateQueries({ queryKey: generalKeys.logFiles })
      void queryClient.invalidateQueries({ queryKey: ['system', 'logs'] })
    },
  })

  return (
    <Section id="files" title="Log files" description="Attach them to a bug report. Deleting the current file empties it.">
      {files.isError && <p className={ui.error}>{files.error.message}</p>}
      {remove.error && <p className={ui.error}>{remove.error.message}</p>}
      {files.data && files.data.length === 0 && <p className={styles.muted}>No log files.</p>}
      {files.data && files.data.length > 0 && (
        <div className={`${styles.table} ${styles.files}`} role="table" aria-label="Log files">
          <div className={styles.head} role="row">
            <span role="columnheader">File</span>
            <span role="columnheader" className={styles.right}>
              Size
            </span>
            <span role="columnheader">Last entry</span>
            <span role="columnheader" />
          </div>
          {files.data.map((file) => (
            <div key={file.name} className={styles.row} role="row">
              <span role="cell" className={`${styles.cell} ${styles.mono}`}>
                {file.name}
                {file.current && <span className={`${ui.pill} ${ui.pillOk} ${styles.inlinePill}`}>current</span>}
              </span>
              <span role="cell" className={`${styles.cell} ${styles.right}`}>
                <span className={styles.cellLabel}>Size</span>
                {formatBytes(file.size)}
              </span>
              <span role="cell" className={styles.cell}>
                <span className={styles.cellLabel}>Last entry</span>
                {formatDateTime(file.lastWrite)}
              </span>
              <span role="cell" className={`${styles.cell} ${styles.actions}`}>
                {confirm === file.name ? (
                  <>
                    <button className={`${ui.danger} ${ui.sm}`} type="button" onClick={() => remove.mutate(file.name)} disabled={remove.isPending}>
                      {file.current ? 'Empty' : 'Delete'}
                    </button>
                    <button className={`${ui.ghost} ${ui.sm}`} type="button" onClick={() => setConfirm(null)}>
                      Cancel
                    </button>
                  </>
                ) : (
                  <>
                    <a className={`${ui.button} ${ui.sm}`} href={generalApi.logFileUrl(file.name)} download={file.name}>
                      <Icon name="download" size={14} />
                      Download
                    </a>
                    <button
                      className={`${ui.ghost} ${ui.sm} ${ui.icon}`}
                      type="button"
                      onClick={() => setConfirm(file.name)}
                      aria-label={`Delete ${file.name}`}
                      title="Delete"
                    >
                      <Icon name="trash" size={14} />
                    </button>
                  </>
                )}
              </span>
            </div>
          ))}
        </div>
      )}
    </Section>
  )
}
