import { useMutation } from '@tanstack/react-query'
import { useId, useState, type FormEvent } from 'react'
import { ApiError } from '../api/client'
import { migrationApi, type MigrationTestResult } from '../api/migration'
import Icon from '../components/Icon'
import Loader from '../components/Loader'
import ui from '../components/ui.module.css'
import styles from './Migration.module.css'
import type { SourceMeta } from './sources'

/** Step 2: address and API key of the old app, a Test button, and Continue (reads the preview). */
export default function ConnectStep({
  source,
  url,
  apiKey,
  onUrl,
  onApiKey,
  fieldErrors,
  formError,
  busy,
  onBack,
  onContinue,
}: {
  source: SourceMeta
  url: string
  apiKey: string
  onUrl: (url: string) => void
  onApiKey: (key: string) => void
  fieldErrors: Record<string, string[]>
  formError: string | null
  busy: boolean
  onBack: () => void
  onContinue: () => void
}) {
  const id = useId()
  const [showKey, setShowKey] = useState(false)
  const [tested, setTested] = useState<{ url: string; apiKey: string; result: MigrationTestResult } | null>(null)
  const [testErrors, setTestErrors] = useState<Record<string, string[]>>({})
  const test = useMutation({
    mutationFn: () => migrationApi.test(source.key, { url, apiKey }),
    onMutate: () => setTestErrors({}),
    onSuccess: (result) => setTested({ url, apiKey, result }),
    onError: (error) => {
      setTested(null)
      if (error instanceof ApiError) setTestErrors(error.fieldErrors)
    },
  })
  // A test result belongs to the values it was made with.
  const result = tested && tested.url === url && tested.apiKey === apiKey ? tested.result : null
  const errors = { ...fieldErrors, ...testErrors }
  const urlError = errors.url?.[0]
  const keyError = errors.apiKey?.[0]
  const testError = test.error && !(test.error instanceof ApiError && Object.keys(test.error.fieldErrors).length > 0) ? test.error.message : null

  const submit = (e: FormEvent) => {
    e.preventDefault()
    onContinue()
  }

  return (
    <form className={styles.stepBody} onSubmit={submit} noValidate>
      <p className={styles.stepLead}>
        StreamZH reads {source.label} through its API. It only reads: nothing in {source.label} is changed, and the API key is not stored.
      </p>
      <div className={styles.connect}>
        <div className={ui.field}>
          <label htmlFor={`${id}-url`}>Address</label>
          <input
            id={`${id}-url`}
            className={`${ui.input} ${urlError ? styles.invalidInput : ''}`}
            type="url"
            inputMode="url"
            autoComplete="off"
            spellCheck={false}
            placeholder={`http://192.168.1.10:${source.port}`}
            value={url}
            aria-invalid={!!urlError}
            aria-describedby={urlError ? `${id}-url-err` : undefined}
            onChange={(e) => (setTestErrors({}), onUrl(e.target.value))}
          />
          {urlError ? (
            <small id={`${id}-url-err`} className={styles.fieldError} role="alert">
              <Icon name="alert" size={13} />
              {urlError}
            </small>
          ) : (
            <small>Include the URL base if {source.label} has one, e.g. /{source.key}.</small>
          )}
        </div>
        <div className={ui.field}>
          <label htmlFor={`${id}-key`}>API key</label>
          <span className={styles.keyWrap}>
            <input
              id={`${id}-key`}
              className={`${ui.input} ${ui.mono} ${keyError ? styles.invalidInput : ''}`}
              type={showKey ? 'text' : 'password'}
              autoComplete="off"
              spellCheck={false}
              value={apiKey}
              aria-invalid={!!keyError}
              aria-describedby={keyError ? `${id}-key-err` : `${id}-key-help`}
              onChange={(e) => (setTestErrors({}), onApiKey(e.target.value))}
            />
            <button
              type="button"
              className={styles.keyEye}
              onClick={() => setShowKey(!showKey)}
              aria-label={showKey ? 'Hide API key' : 'Show API key'}
              aria-pressed={showKey}
            >
              <Icon name={showKey ? 'eyeoff' : 'eye'} size={16} />
            </button>
          </span>
          {keyError ? (
            <small id={`${id}-key-err`} className={styles.fieldError} role="alert">
              <Icon name="alert" size={13} />
              {keyError}
            </small>
          ) : (
            <small id={`${id}-key-help`}>
              In {source.label}: {source.keyHint}.
            </small>
          )}
        </div>
      </div>

      <div className={styles.testLine} aria-live="polite">
        {test.isPending ? (
          <Loader size="row" text={`Connecting to ${source.label}…`} />
        ) : result?.ok && result.app ? (
          <span className={`${ui.pill} ${ui.pillOk}`}>
            <Icon name="check" size={13} />
            {result.app.appName} {result.app.version}
            {result.app.instanceName && result.app.instanceName !== result.app.appName ? ` · ${result.app.instanceName}` : ''}
          </span>
        ) : result && !result.ok ? (
          <span className={styles.testError} role="alert">
            <Icon name="alert" size={14} />
            {result.error ?? `${source.label} did not answer.`}
          </span>
        ) : testError ? (
          <span className={styles.testError} role="alert">
            <Icon name="alert" size={14} />
            {testError}
          </span>
        ) : null}
      </div>

      {formError && (
        <p className={ui.errBox} role="alert">
          <Icon name="alert" size={15} />
          {formError}
        </p>
      )}

      <div className={styles.stepActions}>
        <button type="button" className={ui.ghost} onClick={onBack}>
          <Icon name="left" size={15} />
          Back
        </button>
        <span className={styles.grow} />
        <button type="button" className={ui.button} onClick={() => test.mutate()} disabled={test.isPending || busy}>
          <Icon name="plug" size={15} />
          Test
        </button>
        <button type="submit" className={ui.primary} disabled={busy}>
          {busy ? `Reading ${source.label}…` : 'Continue'}
          {!busy && <Icon name="right" size={15} />}
        </button>
      </div>
      {busy && (
        <div className={styles.reading}>
          <Loader size="row" text={`Reading ${source.label}: profiles, folders and the library. Large libraries take a moment.`} />
        </div>
      )}
    </form>
  )
}
