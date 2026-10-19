import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useRef, useState } from 'react'
import { ApiError } from '../api/client'
import { isLive, migrationApi, migrationKeys, type MigrationPreview, type MigrationRun, type MigrationSource, type PathMapping } from '../api/migration'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import { useAdvanced } from '../lib/advancedMode'
import { buildRequest, carryChoices, countItems, initialChoices, plural, type Choices } from './choices'
import ConnectStep from './ConnectStep'
import { useAutomation, useSetAutomation } from './hooks'
import styles from './Migration.module.css'
import PathsStep from './PathsStep'
import PreviewStep from './PreviewStep'
import RunView from './RunView'
import SourceStep from './SourceStep'
import { migratedSources, nextSource, rememberedMappings, rememberedUrl, rememberMappings, rememberUrl, sourceMeta } from './sources'

const without = (errors: Record<string, string[]>, key: string) => Object.fromEntries(Object.entries(errors).filter(([k]) => k !== key))

type Step = 'source' | 'connect' | 'paths' | 'preview' | 'run'

const cleanMappings = (mappings: PathMapping[]) =>
  mappings.map((m) => ({ remote: m.remote.trim(), local: m.local.trim() })).filter((m) => m.remote && m.local)

/**
 * The migration wizard: Source → Connect → Folders (only when StreamZH cannot see a folder) → Choose → Take over.
 * The API key lives only in this component's state; the address and path mappings are remembered per browser.
 */
export default function Wizard({ runs, resume }: { runs: MigrationRun[] | undefined; resume: number | null }) {
  const queryClient = useQueryClient()
  const advanced = useAdvanced()
  const automation = useAutomation()
  const setAutomation = useSetAutomation()
  const [step, setStep] = useState<Step>(resume ? 'run' : 'source')
  const [source, setSource] = useState<MigrationSource | null>(null)
  const [url, setUrl] = useState('')
  const [apiKey, setApiKey] = useState('')
  const [mappings, setMappings] = useState<PathMapping[]>([])
  const [preview, setPreview] = useState<MigrationPreview | null>(null)
  const [choices, setChoices] = useState<Choices | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({})
  const [runId, setRunId] = useState<number | null>(resume)
  const [planned, setPlanned] = useState<{ key: string; label: string }[]>([])
  const [sideBySide, setSideBySide] = useState<boolean | null>(null)
  const top = useRef<HTMLDivElement>(null)

  const meta = source ? sourceMeta(source) : undefined
  const firstMigration = migratedSources(runs).size === 0
  const hold = automation.data?.hold ?? false
  const sbsOn = sideBySide ?? firstMigration
  const showPaths = !!preview && (preview.paths.some((p) => !p.exists) || cleanMappings(mappings).length > 0)

  const go = (next: Step) => {
    setStep(next)
    // Back to the top of the wizard when it scrolled out of view (the next step starts there).
    if (top.current && top.current.getBoundingClientRect().top < 0) top.current.scrollIntoView({ block: 'start', behavior: 'smooth' })
  }

  const pick = (key: MigrationSource) => {
    setSource(key)
    setUrl(rememberedUrl(key))
    setApiKey('')
    setMappings(rememberedMappings(key))
    setPreview(null)
    setChoices(null)
    setFieldErrors({})
    setSideBySide(null)
    go('connect')
  }

  const read = useMutation({
    mutationFn: (args: { source: MigrationSource; mappings: PathMapping[] }) =>
      // Options picked so far (e.g. "update movies already in StreamZH") shape the preview too when it is read again.
      migrationApi.preview(args.source, { url, apiKey, pathMappings: args.mappings, options: choices?.options }),
    onMutate: () => setFieldErrors({}),
    onSuccess: (result, args) => {
      rememberUrl(args.source, url)
      rememberMappings(args.source, args.mappings)
      setChoices((c) => (c && preview && preview.source === result.source ? carryChoices(c, preview, result) : initialChoices(result)))
      setPreview(result)
      const needsPaths = result.paths.some((p) => !p.exists) || args.mappings.length > 0
      if (step === 'connect') go(needsPaths ? 'paths' : 'preview')
    },
    onError: (error) => {
      if (error instanceof ApiError && (error.fieldErrors.url || error.fieldErrors.apiKey)) {
        setFieldErrors(error.fieldErrors)
        if (step !== 'connect') go('connect')
      }
    },
  })

  const start = useMutation({
    mutationFn: async (dryRun: boolean) => {
      if (!source || !preview || !choices) throw new Error('Nothing to take over.')
      const body = buildRequest({ url, apiKey, pathMappings: cleanMappings(mappings) }, preview, choices, advanced, dryRun, {
        sideBySide: hold || sbsOn,
      })
      if (!dryRun && sbsOn && !hold) await setAutomation.mutateAsync(true)
      return { run: await migrationApi.run(source, body), sections: body.sections ?? [] }
    },
    onSuccess: ({ run, sections }) => {
      setPlanned(sections.map((key) => ({ key, label: preview?.sections.find((s) => s.key === key)?.label ?? key })))
      setRunId(run.id)
      queryClient.setQueryData(migrationKeys.run(run.id), run)
      void queryClient.invalidateQueries({ queryKey: migrationKeys.runs })
      go('run')
    },
    onError: (error) => {
      if (error instanceof ApiError && (error.fieldErrors.url || error.fieldErrors.apiKey)) {
        setFieldErrors(error.fieldErrors)
        go('connect')
      }
    },
  })

  const readError = read.error && !(read.error instanceof ApiError && (read.error.fieldErrors.url || read.error.fieldErrors.apiKey)) ? read.error.message : null
  const startError =
    start.error instanceof ApiError
      ? (start.error.fieldErrors.sections?.[0] ?? (start.error.fieldErrors.url || start.error.fieldErrors.apiKey ? null : start.error.message))
      : start.error
        ? start.error.message
        : null

  const reset = () => {
    setStep('source')
    setSource(null)
    setApiKey('')
    setPreview(null)
    setChoices(null)
    setRunId(null)
    start.reset()
    read.reset()
  }

  // ---- Stepper ----
  const steps: { key: Step; label: string }[] = [
    { key: 'source', label: 'Source' },
    { key: 'connect', label: 'Connect' },
    ...(showPaths ? [{ key: 'paths' as Step, label: 'Folders' }] : []),
    { key: 'preview', label: 'Choose' },
    { key: 'run', label: 'Take over' },
  ]
  const at = steps.findIndex((s) => s.key === step)
  const reachable = (key: Step) => {
    if (step === 'run') return false
    if (key === 'source' || key === 'connect') return key === 'source' || !!source
    return !!preview
  }

  // ---- Choose: the summary and the buttons at the bottom ----
  const selectedSections = preview && choices ? preview.sections.filter((s) => choices.selected.has(s.key) && (advanced || !s.advanced)) : []
  const sum = selectedSections.reduce(
    (acc, s) => {
      const c = countItems(s.items, choices?.excluded ?? new Set())
      return { create: acc.create + c.create, update: acc.update + c.update, link: acc.link + c.link }
    },
    { create: 0, update: 0, link: 0 },
  )

  return (
    <section className={`${ui.panel} ${styles.wizard}`} aria-label="Migration wizard" ref={top}>
      <ol className={styles.stepper}>
        {steps.map((s, i) => {
          const state = i < at ? styles.stepDone : i === at ? styles.stepOn : ''
          const content = (
            <>
              <span className={styles.stepNum}>{i < at ? <Icon name="check" size={12} /> : i + 1}</span>
              <span className={styles.stepName}>{s.label}</span>
            </>
          )
          return (
            <li key={s.key} className={`${styles.stepItem} ${state}`} aria-current={i === at ? 'step' : undefined}>
              {i < at && reachable(s.key) ? (
                <button type="button" className={styles.stepBtn} onClick={() => go(s.key)}>
                  {content}
                </button>
              ) : (
                <span className={styles.stepBtn}>{content}</span>
              )}
            </li>
          )
        })}
        {meta && step !== 'source' && (
          <li className={styles.stepSource}>
            <Icon name={meta.icon} size={14} />
            {meta.label}
          </li>
        )}
      </ol>

      {step === 'source' && <SourceStep runs={runs} onPick={pick} />}

      {step === 'connect' && meta && (
        <ConnectStep
          source={meta}
          url={url}
          apiKey={apiKey}
          onUrl={(v) => (setUrl(v), setFieldErrors((e) => without(e, 'url')))}
          onApiKey={(v) => (setApiKey(v), setFieldErrors((e) => without(e, 'apiKey')))}
          fieldErrors={fieldErrors}
          formError={readError}
          busy={read.isPending}
          onBack={() => go('source')}
          onContinue={() => source && read.mutate({ source, mappings: cleanMappings(mappings) })}
        />
      )}

      {step === 'paths' && meta && preview && (
        <PathsStep
          source={meta}
          paths={preview.paths}
          mappings={mappings}
          onMappings={setMappings}
          busy={read.isPending}
          error={readError}
          onCheck={() => source && read.mutate({ source, mappings: cleanMappings(mappings) })}
          onBack={() => go('connect')}
          onContinue={() => go('preview')}
        />
      )}

      {step === 'preview' && meta && preview && choices && (
        <>
          <PreviewStep
            source={meta}
            preview={preview}
            choices={choices}
            onChoices={(update) => setChoices((c) => (c ? update(c) : c))}
            advanced={advanced}
            sideBySide={hold || sbsOn}
            onEditPaths={() => go('paths')}
          />
          <div className={styles.runBar}>
            <div className={styles.runSummary}>
              {selectedSections.length === 0 ? (
                <span className={ui.muted}>Pick at least one part.</span>
              ) : (
                <>
                  <b>{plural(selectedSections.length, 'part')}</b>
                  <span className={ui.muted}>
                    {[sum.create && `${sum.create.toLocaleString()} new`, sum.update && `${sum.update.toLocaleString()} updated`, sum.link && `${sum.link.toLocaleString()} linked`]
                      .filter(Boolean)
                      .join(' · ') || 'nothing new'}
                  </span>
                </>
              )}
            </div>
            {hold ? (
              <p className={styles.sbsNote}>
                <Icon name="pause" size={14} />
                Side by side is on: StreamZH grabs nothing automatically until you switch over.
              </p>
            ) : (
              !automation.isError &&
              automation.data && (
                <label className={styles.sbsSwitch}>
                  <span className={ui.switch}>
                    <input type="checkbox" role="switch" checked={sbsOn} onChange={(e) => setSideBySide(e.target.checked)} />
                    <span aria-hidden="true" />
                  </span>
                  <span className={styles.sbsBody}>
                    <span className={styles.sbsTitle}>
                      <b>Run side by side</b>
                      {firstMigration && <span className={styles.recommended}>recommended</span>}
                    </span>
                    <small>StreamZH takes everything over but grabs nothing automatically until you switch over.</small>
                  </span>
                </label>
              )
            )}
            {startError && (
              <p className={ui.errBox} role="alert">
                <Icon name="alert" size={15} />
                {startError}
              </p>
            )}
            <div className={styles.stepActions}>
              <button type="button" className={ui.ghost} onClick={() => go(showPaths ? 'paths' : 'connect')}>
                <Icon name="left" size={15} />
                Back
              </button>
              <span className={styles.grow} />
              {advanced && (
                <button
                  type="button"
                  className={ui.button}
                  onClick={() => start.mutate(true)}
                  disabled={start.isPending || selectedSections.length === 0}
                  title="Goes through everything without changing anything, and shows the report."
                >
                  <Icon name="eye" size={15} />
                  Dry run
                </button>
              )}
              <button type="button" className={ui.primary} onClick={() => start.mutate(false)} disabled={start.isPending || selectedSections.length === 0}>
                <Icon name="mountin" size={15} />
                {start.isPending ? 'Starting…' : 'Take over'}
              </button>
            </div>
          </div>
        </>
      )}

      {step === 'run' && runId != null && (
        <div className={styles.stepBody}>
          <RunView
            id={runId}
            planned={planned}
            actions={(run) => {
              const next = nextSource(runs, run.status === 'done' && !run.dryRun ? run.source : undefined)
              return (
                <>
                  <button type="button" className={ui.ghost} onClick={reset}>
                    <Icon name="left" size={15} />
                    Start over
                  </button>
                  <span className={styles.grow} />
                  {preview && source === run.source && (run.dryRun || run.status !== 'done') && (
                    <button type="button" className={ui.button} onClick={() => go('preview')}>
                      Back to the choices
                    </button>
                  )}
                  {run.dryRun && run.status === 'done' && preview && source === run.source ? (
                    <button type="button" className={ui.primary} onClick={() => start.mutate(false)} disabled={start.isPending}>
                      <Icon name="mountin" size={15} />
                      Take over now
                    </button>
                  ) : (
                    next &&
                    !isLive(run) &&
                    run.status === 'done' &&
                    !run.dryRun && (
                      <button type="button" className={ui.primary} onClick={() => pick(next.key)}>
                        Next: {next.label}
                        <Icon name="right" size={15} />
                      </button>
                    )
                  )}
                </>
              )
            }}
          />
        </div>
      )}
    </section>
  )
}
