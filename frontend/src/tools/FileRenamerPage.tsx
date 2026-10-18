import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useMemo, useState } from 'react'
import { ApiError } from '../api/client'
import { libraryApi, queryKeys } from '../api/library'
import { mediaManagementApi, mediaManagementKeys } from '../api/mediaManagement'
import { renamerApi, renamerKeys, type ExtensionPreset, type RenamerFilter, type RenamerOptions, type RenamerRequest, type RenamerSource } from '../api/renamer'
import FolderPicker from '../components/FolderPicker'
import Icon from '../components/Icon'
import Loader from '../components/Loader'
import PageHeader from '../components/PageHeader'
import ui from '../components/ui.module.css'
import { AdvancedToggle } from '../lib/Advanced'
import { useAdvanced } from '../lib/advancedMode'
import HistoryPanel from './renamer/HistoryPanel'
import PreviewTable from './renamer/PreviewTable'
import styles from './renamer/Renamer.module.css'
import { Check, Field } from './renamer/RuleForms'
import RuleList from './renamer/RuleList'
import { activeStates, defaultFilter, defaultOptions, defaultSimple, defaultSource, folderFormats, fromApiRules, newRule, toApiRules, type EditorRule, type SimpleSetup } from './renamer/rules'
import SimpleName from './renamer/SimpleName'
import { useDebounced } from './renamer/useDebounced'

const draftKey = 'streamzh-renamer-draft'

interface Draft {
  mode: 'folder' | 'library'
  source: RenamerSource
  filter: RenamerFilter
  rules: EditorRule[]
  options: RenamerOptions
}

/** The last setup in this browser (storage may be missing or blocked). */
function readDraft(): Draft | null {
  try {
    const text = localStorage.getItem(draftKey)
    if (!text) return null
    const draft = JSON.parse(text) as Partial<Draft>
    return {
      mode: draft.mode === 'library' ? 'library' : 'folder',
      source: { ...defaultSource, ...draft.source },
      filter: { ...defaultFilter, ...draft.filter },
      rules: fromApiRules(draft.rules ?? []),
      options: { ...defaultOptions, ...draft.options },
    }
  } catch {
    return null
  }
}

function writeDraft(draft: Draft) {
  try {
    localStorage.setItem(draftKey, JSON.stringify({ ...draft, rules: toApiRules(draft.rules) }))
  } catch {
    // no storage: the page still works, it just forgets
  }
}

const simpleKey = 'streamzh-renamer-simple'

/** The simple setup of this browser. */
function readSimple(): SimpleSetup {
  try {
    const text = localStorage.getItem(simpleKey)
    if (!text) return defaultSimple
    const saved = JSON.parse(text) as Partial<SimpleSetup>
    return {
      kind: saved.kind === 'movie' ? 'movie' : 'episode',
      episodeFormat: saved.episodeFormat?.trim() || defaultSimple.episodeFormat,
      movieFormat: saved.movieFormat?.trim() || defaultSimple.movieFormat,
      seasonFolders: saved.seasonFolders ?? defaultSimple.seasonFolders,
    }
  } catch {
    return defaultSimple
  }
}

function writeSimple(setup: SimpleSetup) {
  try {
    localStorage.setItem(simpleKey, JSON.stringify(setup))
  } catch {
    // no storage
  }
}

/** "Season {season:00}" → "Season 01", for the checkbox label. */
const seasonExample = (format: string) =>
  format.replace(/\{season:0+\}/gi, '01').replace(/\{season\}/gi, '1').replace(/\{[^}]*\}/g, '').trim() || null

/** The simple setup as one media rule (titles from the library, then TMDB). */
function simpleRule(setup: SimpleSetup, kind: SimpleSetup['kind']): EditorRule {
  return {
    ...(newRule('media') as Extract<EditorRule, { type: 'media' }>),
    kind,
    episodeFormat: setup.episodeFormat,
    movieFormat: setup.movieFormat,
  }
}

/** "about 20 s left" from the start time and the progress of a run. */
function timeLeft(started: string, done: number, total: number): string | null {
  if (done <= 0 || done >= total) return null
  const elapsed = (Date.now() - new Date(started).getTime()) / 1000
  if (!(elapsed > 0)) return null
  const left = Math.max(1, Math.round((elapsed / done) * (total - done)))
  return left < 60 ? `about ${left} s left` : `about ${Math.round(left / 60)} min left`
}

const extensionPresets: { value: ExtensionPreset; label: string }[] = [
  { value: 'video', label: 'Video' },
  { value: 'videoAndSubtitles', label: 'Video + subtitles' },
  { value: 'subtitles', label: 'Subtitles' },
  { value: 'images', label: 'Images' },
  { value: 'all', label: 'All files' },
  { value: 'custom', label: 'Custom' },
]

/**
 * Tools → File renamer: a bulk renamer like Advanced Renamer or FileBot. Pick a folder (or a library item), filter the
 * files, stack rules (with StreamZH's media rule for episode and movie names), check the preview and rename. Every run
 * is logged and can be undone.
 */
export default function FileRenamerPage() {
  const queryClient = useQueryClient()
  const advanced = useAdvanced()
  const [initial] = useState(readDraft)
  const [mode, setMode] = useState<'folder' | 'library'>(initial?.mode ?? 'folder')
  const [source, setSource] = useState<RenamerSource>(initial?.source ?? defaultSource)
  const [filter, setFilter] = useState<RenamerFilter>(initial?.filter ?? defaultFilter)
  const [rules, setRules] = useState<EditorRule[]>(initial?.rules ?? [])
  const [options, setOptions] = useState<RenamerOptions>(initial?.options ?? defaultOptions)
  const [excluded, setExcluded] = useState<Set<string>>(new Set())
  const [picking, setPicking] = useState(false)
  const [typedPath, setTypedPath] = useState(initial?.source.path ?? '')
  const [presetName, setPresetName] = useState<string | null>(null)
  const [presetId, setPresetId] = useState<number | null>(null)
  const [lastRun, setLastRun] = useState<number | null>(null)
  const [simple, setSimple] = useState<SimpleSetup>(readSimple)

  useEffect(() => writeDraft({ mode, source, filter, rules, options }), [mode, source, filter, rules, options])
  useEffect(() => writeSimple(simple), [simple])

  const effectiveSource: RenamerSource = useMemo(
    () => (mode === 'folder' ? { ...source, movieId: null, seriesId: null } : { ...source, path: null }),
    [mode, source],
  )
  const hasSource = mode === 'folder' ? !!source.path : source.movieId != null || source.seriesId != null

  const settings = useQuery({ queryKey: mediaManagementKeys.settings, queryFn: mediaManagementApi.settings })
  const seasonFormat = settings.data?.seasonFolderFormat || 'Season {season}'

  // Simple mode: a movie or series from the library decides episodes or movies.
  const kindFixed = mode === 'library' && (source.seriesId != null || source.movieId != null)
  const simpleKind: SimpleSetup['kind'] = kindFixed ? (source.seriesId != null ? 'episode' : 'movie') : simple.kind

  // Turning on Advanced with no rules yet starts from the simple pattern.
  useEffect(() => {
    if (advanced) setRules((r) => (r.length === 0 ? [simpleRule(simple, simpleKind)] : r))
    // only when the switch changes
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [advanced])

  const request: RenamerRequest = useMemo(
    () =>
      advanced
        ? { source: effectiveSource, filter, rules: toApiRules(rules), options, excluded: [...excluded] }
        : {
            source: { ...effectiveSource, depth: defaultSource.depth, target: 'files' },
            filter: defaultFilter,
            rules: toApiRules([simpleRule(simple, simpleKind)]),
            options: { folderFormat: simpleKind === 'episode' && simple.seasonFolders ? seasonFormat : '', companions: true },
            excluded: [...excluded],
          },
    [advanced, effectiveSource, filter, rules, options, excluded, simple, simpleKind, seasonFormat],
  )
  const requestKey = useDebounced(JSON.stringify(request), 350)
  // The debounced request can still be the one from before a folder was chosen.
  const debouncedHasSource = useMemo(() => {
    const { source: s } = JSON.parse(requestKey) as RenamerRequest
    return !!s.path || s.movieId != null || s.seriesId != null
  }, [requestKey])

  const preview = useQuery({
    queryKey: renamerKeys.preview(requestKey),
    queryFn: () => renamerApi.preview(JSON.parse(requestKey) as RenamerRequest),
    enabled: hasSource && debouncedHasSource,
    placeholderData: keepPreviousData,
    retry: false,
  })

  const settingsFormats = settings.data ? { episode: settings.data.standardEpisodeFormat, movie: settings.data.standardMovieFormat } : null
  const movies = useQuery({ queryKey: queryKeys.movies, queryFn: libraryApi.movies, enabled: mode === 'library' })
  const series = useQuery({ queryKey: queryKeys.series, queryFn: libraryApi.series, enabled: mode === 'library' })
  const presets = useQuery({ queryKey: renamerKeys.presets, queryFn: renamerApi.presets })
  const history = useQuery({
    queryKey: renamerKeys.history,
    queryFn: () => renamerApi.history(30),
    refetchInterval: (query) => (query.state.data?.some((r) => activeStates.includes(r.state)) ? 1000 : false),
  })
  const runs = history.data ?? []
  const active = runs.find((r) => activeStates.includes(r.state))
  const finished = lastRun != null ? runs.find((r) => r.id === lastRun && !activeStates.includes(r.state)) : undefined

  // A run that just ended: the names on disk changed, so the preview is stale.
  const activeId = active?.id
  useEffect(() => {
    if (activeId == null) void queryClient.invalidateQueries({ queryKey: ['renamer', 'preview'] })
  }, [activeId, queryClient])

  const apply = useMutation({
    mutationFn: () => renamerApi.apply(request),
    onSuccess: (result) => {
      setLastRun(result.runId)
      setExcluded(new Set())
      void queryClient.invalidateQueries({ queryKey: renamerKeys.history })
    },
  })

  const savePreset = useMutation({
    mutationFn: (name: string) => {
      const content = { filter, rules: toApiRules(rules), options }
      const existing = presets.data?.find((p) => p.name.toLowerCase() === name.trim().toLowerCase())
      return existing ? renamerApi.updatePreset(existing.id, name, content) : renamerApi.addPreset(name, content)
    },
    onSuccess: (preset) => {
      setPresetName(null)
      setPresetId(preset.id)
      void queryClient.invalidateQueries({ queryKey: renamerKeys.presets })
    },
  })
  const deletePreset = useMutation({
    mutationFn: (id: number) => renamerApi.deletePreset(id),
    onSuccess: () => {
      setPresetId(null)
      void queryClient.invalidateQueries({ queryKey: renamerKeys.presets })
    },
  })

  const loadPreset = (id: number) => {
    const preset = presets.data?.find((p) => p.id === id)
    if (!preset) return
    setPresetId(id)
    setFilter({ ...defaultFilter, ...preset.content.filter })
    setRules(fromApiRules(preset.content.rules))
    setOptions({ ...defaultOptions, ...preset.content.options })
    setExcluded(new Set())
  }

  const fieldErrors = preview.error instanceof ApiError ? preview.error.fieldErrors : {}
  const sourceError = fieldErrors['source.path']?.[0] ?? fieldErrors['source.depth']?.[0] ?? (preview.error && !(preview.error instanceof ApiError && Object.keys(preview.error.fieldErrors).length > 0) ? preview.error.message : null)
  const data = preview.data
  const sample = data?.rows.find((r) => !r.isFolder && !r.companionOf)?.oldName ?? null
  const exampleRow = data?.rows.find((r) => !r.isFolder && !r.companionOf && (r.status === 'rename' || r.status === 'unchanged'))
  const example = hasSource && exampleRow ? exampleRow.newName : null
  const libraryFiles = data?.rows.filter((r) => r.status === 'rename' && (r.movieId != null || r.seriesId != null)).length ?? 0
  const ruleErrors = data?.ruleErrors ?? {}
  const blocked = Object.keys(ruleErrors).length > 0 || !!data?.folderFormatError
  const renameCount = data?.counts.rename ?? 0
  const setFilterField = <K extends keyof RenamerFilter>(key: K, value: RenamerFilter[K]) => setFilter((f) => ({ ...f, [key]: value }))

  const chosenLibrary = source.seriesId != null ? `series:${source.seriesId}` : source.movieId != null ? `movie:${source.movieId}` : ''

  return (
    <div className={styles.page}>
      <PageHeader
        eyebrow="Tools"
        title="File renamer"
        lead={
          advanced
            ? 'Rename many files at once with rules, filters and regular expressions. Episodes and movies get their real titles. Nothing changes before you press Rename, and every run can be undone.'
            : 'Give episodes and movies clean names with their real titles. Nothing changes before you press Rename, and every run can be undone.'
        }
        actions={
          <>
          {advanced && (
            <>
              <select
                className={`${ui.select} ${styles.presetSelect}`}
                value={presetId ?? ''}
                aria-label="Preset"
                onChange={(e) => e.target.value && loadPreset(Number(e.target.value))}
              >
                <option value="">{presets.data?.length ? 'Load a preset…' : 'No presets yet'}</option>
                {presets.data?.map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.name}
                  </option>
                ))}
              </select>
              <button type="button" className={ui.button} onClick={() => setPresetName(presets.data?.find((p) => p.id === presetId)?.name ?? '')} disabled={rules.length === 0}>
                <Icon name="save" size={15} />
                Save preset
              </button>
            </>
          )}
          <AdvancedToggle />
          </>
        }
      />

      {advanced && presetName != null && (
        <form
          className={styles.presetForm}
          onSubmit={(e) => {
            e.preventDefault()
            if (presetName.trim()) savePreset.mutate(presetName.trim())
          }}
        >
          <Field label="Preset name">
            <input className={ui.input} value={presetName} autoFocus maxLength={100} onChange={(e) => setPresetName(e.target.value)} placeholder="Anime seasons" />
          </Field>
          <span className={styles.presetHint}>Keeps the filter, the rules and the options, not the folder. Same name replaces the preset.</span>
          <div className={styles.presetButtons}>
            {presetId != null && (
              <button type="button" className={ui.button} disabled={deletePreset.isPending} onClick={() => deletePreset.mutate(presetId)}>
                Delete preset
              </button>
            )}
            <button type="button" className={ui.button} onClick={() => setPresetName(null)}>
              Cancel
            </button>
            <button type="submit" className={ui.primary} disabled={!presetName.trim() || savePreset.isPending}>
              {savePreset.isPending ? 'Saving…' : 'Save'}
            </button>
          </div>
          {savePreset.error && <p className={ui.error}>{savePreset.error.message}</p>}
        </form>
      )}

      <div className={styles.setup}>
        <div className={styles.column}>
          {/* 1 Source */}
          <section className={styles.card} aria-labelledby="renamer-source">
            <h2 id="renamer-source" className={styles.cardTitle}>
              <span className={styles.step}>1</span> Source
            </h2>
            <div className={styles.segmented} role="tablist" aria-label="Source">
              <button type="button" role="tab" aria-selected={mode === 'folder'} className={mode === 'folder' ? styles.segmentOn : ''} onClick={() => setMode('folder')}>
                Folder
              </button>
              <button type="button" role="tab" aria-selected={mode === 'library'} className={mode === 'library' ? styles.segmentOn : ''} onClick={() => setMode('library')}>
                Movie or series
              </button>
            </div>
            {mode === 'folder' ? (
              <form
                className={styles.pathRow}
                onSubmit={(e) => {
                  e.preventDefault()
                  setSource((s) => ({ ...s, path: typedPath.trim() || null }))
                  setExcluded(new Set())
                }}
              >
                <input
                  className={`${ui.input} ${ui.mono}`}
                  value={typedPath}
                  spellCheck={false}
                  aria-label="Folder"
                  placeholder="/media/downloads/Show"
                  onChange={(e) => setTypedPath(e.target.value)}
                  onBlur={() => setSource((s) => ({ ...s, path: typedPath.trim() || null }))}
                />
                <button type="button" className={ui.button} onClick={() => setPicking(true)}>
                  <Icon name="folder" size={15} />
                  Browse…
                </button>
              </form>
            ) : (
              <select
                className={ui.select}
                value={chosenLibrary}
                aria-label="Movie or series"
                onChange={(e) => {
                  const [kind, id] = e.target.value.split(':')
                  setSource((s) => ({ ...s, movieId: kind === 'movie' ? Number(id) : null, seriesId: kind === 'series' ? Number(id) : null }))
                  setExcluded(new Set())
                }}
              >
                <option value="">Choose a movie or series…</option>
                {series.data && series.data.length > 0 && (
                  <optgroup label="Series">
                    {series.data.map((s) => (
                      <option key={s.id} value={`series:${s.id}`}>
                        {s.title} ({s.year})
                      </option>
                    ))}
                  </optgroup>
                )}
                {movies.data && movies.data.length > 0 && (
                  <optgroup label="Movies">
                    {movies.data.map((m) => (
                      <option key={m.id} value={`movie:${m.id}`}>
                        {m.title} ({m.year})
                      </option>
                    ))}
                  </optgroup>
                )}
              </select>
            )}
            {sourceError && hasSource && <p className={ui.error}>{sourceError}</p>}
            {data && hasSource && !sourceError && mode === 'library' && <p className={styles.sourceNote}>{data.folder}</p>}
            <div className={styles.checks}>
              <Check checked={source.subfolders} onChange={(subfolders) => setSource((s) => ({ ...s, subfolders }))}>
                Include subfolders
              </Check>
            </div>
            {advanced && (
              <div className={styles.fields}>
                {source.subfolders && (
                  <Field label="Levels">
                    <input
                      className={`${ui.input} ${styles.num}`}
                      type="number"
                      min={1}
                      max={20}
                      value={source.depth}
                      onChange={(e) => setSource((s) => ({ ...s, depth: Math.min(20, Math.max(1, Math.trunc(e.target.valueAsNumber) || 1)) }))}
                    />
                  </Field>
                )}
                <Field label="Rename">
                  <select className={ui.select} value={source.target} onChange={(e) => setSource((s) => ({ ...s, target: e.target.value as RenamerSource['target'] }))}>
                    <option value="files">Files</option>
                    <option value="filesAndFolders">Files and folders</option>
                    <option value="folders">Folders only</option>
                  </select>
                </Field>
              </div>
            )}
          </section>

          {/* 2 Filter */}
          {advanced && (
            <section className={styles.card} aria-labelledby="renamer-filter">
              <h2 id="renamer-filter" className={styles.cardTitle}>
                <span className={styles.step}>2</span> Filter
                {data && hasSource && (
                  <span className={styles.cardCount}>
                    {data.matched} of {data.total} match
                  </span>
                )}
              </h2>
              <div className={styles.chips} role="group" aria-label="File types">
                {extensionPresets.map((p) => (
                  <button key={p.value} type="button" aria-pressed={filter.extensions === p.value} className={`${styles.chip} ${filter.extensions === p.value ? styles.chipOn : ''}`} onClick={() => setFilterField('extensions', p.value)}>
                    {p.label}
                  </button>
                ))}
              </div>
              {filter.extensions === 'custom' && (
                <Field label="Extensions" hint={fieldErrors['filter.customExtensions']?.[0] ?? 'Separated by commas, e.g. mkv, mp4, srt.'}>
                  <input className={`${ui.input} ${ui.mono}`} value={filter.customExtensions} onChange={(e) => setFilterField('customExtensions', e.target.value)} placeholder="mkv, mp4, srt" />
                </Field>
              )}
              <div className={styles.fields}>
                <Field label="Name contains" hint={fieldErrors['filter.include']?.[0] ? <span className={ui.error}>{fieldErrors['filter.include'][0]}</span> : <>Text, a pattern like *S01* (several with ;) or /regex/</>}>
                  <input className={`${ui.input} ${ui.mono}`} value={filter.include} spellCheck={false} onChange={(e) => setFilterField('include', e.target.value)} placeholder="*S01*" />
                </Field>
                <Field label="Leave out" hint={fieldErrors['filter.exclude']?.[0] ? <span className={ui.error}>{fieldErrors['filter.exclude'][0]}</span> : undefined}>
                  <input className={`${ui.input} ${ui.mono}`} value={filter.exclude} spellCheck={false} onChange={(e) => setFilterField('exclude', e.target.value)} placeholder="*trailer*" />
                </Field>
                {advanced && (
                  <>
                    <Field label="Minimum size (MB)">
                      <input className={`${ui.input} ${styles.num}`} type="number" min={0} value={filter.minSizeMb ?? ''} onChange={(e) => setFilterField('minSizeMb', e.target.value === '' ? null : e.target.valueAsNumber)} />
                    </Field>
                    <Field label="Maximum size (MB)" hint={fieldErrors['filter.maxSizeMb']?.[0]}>
                      <input className={`${ui.input} ${styles.num}`} type="number" min={0} value={filter.maxSizeMb ?? ''} onChange={(e) => setFilterField('maxSizeMb', e.target.value === '' ? null : e.target.valueAsNumber)} />
                    </Field>
                  </>
                )}
              </div>
              <div className={styles.checks}>
                <Check checked={filter.hideSamples} onChange={(hideSamples) => setFilterField('hideSamples', hideSamples)}>
                  Leave out samples
                </Check>
              </div>
            </section>
          )}
        </div>

        {!advanced ? (
          <section className={styles.card} aria-labelledby="renamer-name">
            <h2 id="renamer-name" className={styles.cardTitle}>
              <span className={styles.step}>2</span> New name
            </h2>
            <SimpleName setup={{ ...simple, kind: simpleKind }} onChange={setSimple} kindFixed={kindFixed} seasonFolder={seasonExample(seasonFormat)} example={example} />
          </section>
        ) : (
          /* 3 Rules */
          <section className={`${styles.card} ${styles.rulesCard}`} aria-labelledby="renamer-rules">
            <h2 id="renamer-rules" className={styles.cardTitle}>
              <span className={styles.step}>3</span> Rules
              {rules.length > 0 && <span className={styles.cardCount}>run from top to bottom</span>}
            </h2>
            <RuleList rules={rules} onChange={setRules} errors={ruleErrors} sample={sample} settingsFormats={settingsFormats} />
          </section>
        )}
      </div>

      {/* 4 Preview */}
      <section className={`${styles.card} ${styles.previewCard}`} aria-labelledby="renamer-preview">
        <h2 id="renamer-preview" className={styles.cardTitle}>
          <span className={styles.step}>{advanced ? 4 : 3}</span> Preview
          {preview.isFetching && (
            <span className={styles.cardCount}>
              <Loader size="row" text="Updating…" />
            </span>
          )}
        </h2>
        {advanced && (
          <div className={styles.fields}>
            <Field
              label="Move into a folder"
              hint={data?.folderFormatError ? <span className={ui.error}>{data.folderFormatError}</span> : 'A subfolder of each file’s folder, e.g. Season {season:00}. Empty: files stay where they are.'}
            >
              <input className={`${ui.input} ${ui.mono}`} value={options.folderFormat} spellCheck={false} list="renamer-folder-formats" placeholder="No folder" onChange={(e) => setOptions((o) => ({ ...o, folderFormat: e.target.value }))} />
              <datalist id="renamer-folder-formats">
                {folderFormats.map((f) => (
                  <option key={f.value} value={f.value}>
                    {f.label}
                  </option>
                ))}
              </datalist>
            </Field>
          </div>
        )}
        {advanced && (
          <div className={styles.checks}>
            <Check checked={options.companions} onChange={(companions) => setOptions((o) => ({ ...o, companions }))}>
              Subtitles and other files with the video’s name follow it
            </Check>
          </div>
        )}

        {!hasSource ? (
          <p className={styles.emptyRows}>Choose a folder or a movie or series to see the files.</p>
        ) : data ? (
          <PreviewTable
            preview={data}
            nothingText={advanced ? undefined : 'All names already fit the pattern.'}
            excluded={excluded}
            onToggle={(key, include) =>
              setExcluded((s) => {
                const next = new Set(s)
                if (include) next.delete(key)
                else next.add(key)
                return next
              })
            }
            onToggleAll={(keys, include) =>
              setExcluded((s) => {
                const next = new Set(s)
                for (const key of keys) {
                  if (include) next.delete(key)
                  else next.add(key)
                }
                return next
              })
            }
          />
        ) : preview.isPending && !sourceError ? (
          <Loader text="Reading the folder…" />
        ) : null}

        <div className={styles.applyBar}>
          <div className={styles.applyText} aria-live="polite">
            {active ? (
              <span className={styles.progress}>
                <span>
                  {active.state === 'undoing' ? 'Undoing…' : `Renaming ${active.done} of ${active.total}…`}
                </span>
                {active.total > 0 && <span className={styles.percent}>{Math.round((active.done / active.total) * 100)} %</span>}
                <span className={styles.progressTrack}>
                  <span className={styles.progressFill} style={{ width: `${active.total > 0 ? Math.round((active.done / active.total) * 100) : 0}%` }} />
                </span>
                {timeLeft(active.started, active.done, active.total) && <span className={ui.muted}>{timeLeft(active.started, active.done, active.total)}</span>}
              </span>
            ) : finished ? (
              <span>
                {finished.state === 'completed' ? (
                  <span className={ui.ok}>Renamed {finished.done} file{finished.done === 1 ? '' : 's'}.</span>
                ) : finished.state === 'undone' ? (
                  'Undone.'
                ) : (
                  <span className={ui.error}>
                    {finished.done} renamed, {finished.failed} failed{finished.error ? `: ${finished.error}` : ''}. See History.
                  </span>
                )}
              </span>
            ) : blocked ? (
              <span className={ui.error}>Fix the marked rule first.</span>
            ) : libraryFiles > 0 ? (
              <span>
                {libraryFiles} file{libraryFiles === 1 ? ' belongs' : 's belong'} to the library; the library follows the new names.
              </span>
            ) : data && data.counts.conflict > 0 ? (
              <span>Files with a conflict are left as they are.</span>
            ) : null}
            {apply.error && <span className={ui.error}>{apply.error.message}</span>}
          </div>
          <button type="button" className={ui.primary} disabled={!hasSource || renameCount === 0 || blocked || apply.isPending || !!active} onClick={() => apply.mutate()}>
            <Icon name="rename" size={15} />
            {apply.isPending ? 'Starting…' : `Rename ${renameCount} file${renameCount === 1 ? '' : 's'}`}
          </button>
        </div>
      </section>

      <section className={styles.card} aria-labelledby="renamer-history">
        <h2 id="renamer-history" className={styles.cardTitle}>
          <Icon name="history" size={17} className={styles.titleIcon} />
          History
          {runs.length > 0 && <span className={styles.cardCount}>{runs.length} run{runs.length === 1 ? '' : 's'}</span>}
        </h2>
        {history.isError ? <p className={ui.error}>{history.error.message}</p> : <HistoryPanel runs={runs} />}
      </section>

      {picking && (
        <FolderPicker
          initialPath={source.path ?? undefined}
          onClose={() => setPicking(false)}
          onSelect={(path) => {
            setPicking(false)
            setTypedPath(path)
            setSource((s) => ({ ...s, path }))
            setExcluded(new Set())
          }}
        />
      )}
    </div>
  )
}
