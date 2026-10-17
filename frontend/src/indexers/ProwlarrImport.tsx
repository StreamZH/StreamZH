import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useId, useState, type FormEvent } from 'react'
import { ApiError } from '../api/client'
import { indexerKeys, indexersApi, type ProwlarrConnection, type ProwlarrImportMode, type ProwlarrPreviewItem } from '../api/indexers'
import ui from '../components/ui.module.css'
import { Pill, ProtocolChip } from './Chips'
import Dialog from './Dialog'
import { hostOf } from './model'
import styles from './Import.module.css'

const steps = ['Connect', 'Choose', 'Done'] as const

const modeText: Record<ProwlarrImportMode, { label: string; tone: 'ok' | 'info'; help: string }> = {
  direct: {
    label: 'Direct',
    tone: 'ok',
    help: 'StreamZH talks to the indexer itself, with the indexer’s own URL and API key read from Prowlarr.',
  },
  proxy: {
    label: 'Via Prowlarr',
    tone: 'info',
    help: 'Prowlarr does not hand out indexer API keys. Without the indexer’s own key, StreamZH searches through Prowlarr, which then has to keep running. Paste the key from your account page at the indexer to use it directly and switch Prowlarr off.',
  },
}

function selectable(item: ProwlarrPreviewItem) {
  return item.supported && !item.alreadyExists
}

/** Wizard: connect to Prowlarr, pick indexers from the preview, import them. */
export default function ProwlarrImport({ onClose, onTestAll }: { onClose: () => void; onTestAll: () => void }) {
  const queryClient = useQueryClient()
  const formId = useId()
  const [connection, setConnection] = useState<ProwlarrConnection>({ url: '', apiKey: '' })
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const [ownKeys, setOwnKeys] = useState<Record<number, string>>({})
  const modeOf = (item: ProwlarrPreviewItem): ProwlarrImportMode => (ownKeys[item.prowlarrId]?.trim() ? 'direct' : item.mode)
  const [step, setStep] = useState<0 | 1 | 2>(0)

  const preview = useMutation({
    mutationFn: () => indexersApi.prowlarrPreview(connection),
    onSuccess: (items) => {
      setSelected(new Set(items.filter(selectable).map((i) => i.prowlarrId)))
      setStep(1)
    },
  })
  const importer = useMutation({
    mutationFn: () =>
      indexersApi.prowlarrImport(
        connection,
        [...selected],
        Object.fromEntries(Object.entries(ownKeys).filter(([, key]) => key.trim() !== '')),
      ),
    onSuccess: async () => {
      setStep(2)
      await queryClient.invalidateQueries({ queryKey: indexerKeys.all })
    },
  })

  const items = preview.data ?? []
  const choosable = items.filter(selectable)
  const allChosen = choosable.length > 0 && choosable.every((i) => selected.has(i.prowlarrId))
  const modes = new Set(items.filter(selectable).map(modeOf))

  const toggle = (id: number, on: boolean) =>
    setSelected((s) => {
      const next = new Set(s)
      if (on) next.add(id)
      else next.delete(id)
      return next
    })

  const connectErrors = preview.error instanceof ApiError ? preview.error.fieldErrors : {}
  const fieldError = (name: string) => connectErrors[name]?.[0]

  const submitConnect = (e: FormEvent) => {
    e.preventDefault()
    preview.mutate()
  }

  const footer =
    step === 0 ? (
      <>
        <span className={styles.spacer} />
        <button className={ui.button} type="button" onClick={onClose}>
          Cancel
        </button>
        <button className={ui.primary} type="submit" form={formId} disabled={preview.isPending || !connection.url.trim() || !connection.apiKey.trim()}>
          {preview.isPending ? 'Connecting…' : 'Next'}
        </button>
      </>
    ) : step === 1 ? (
      <>
        <button className={ui.button} type="button" onClick={() => setStep(0)}>
          ← Back
        </button>
        <span className={styles.spacer} />
        <button className={ui.button} type="button" onClick={onClose}>
          Cancel
        </button>
        <button className={ui.primary} type="button" onClick={() => importer.mutate()} disabled={importer.isPending || selected.size === 0}>
          {importer.isPending ? 'Importing…' : `Import ${selected.size} indexer${selected.size === 1 ? '' : 's'}`}
        </button>
      </>
    ) : (
      <>
        <span className={styles.spacer} />
        {(importer.data?.imported.length ?? 0) > 0 && (
          <button className={ui.button} type="button" onClick={onTestAll}>
            Test all
          </button>
        )}
        <button className={ui.primary} type="button" onClick={onClose}>
          Done
        </button>
      </>
    )

  return (
    <Dialog
      title="Import from Prowlarr"
      size={step === 1 ? 'wide' : 'normal'}
      onClose={onClose}
      footer={footer}
      subtitle={
        <ol className={styles.steps} aria-label="Steps">
          {steps.map((label, index) => (
            <li
              key={label}
              className={index === step ? styles.current : index < step ? styles.doneStep : undefined}
              aria-current={index === step ? 'step' : undefined}
            >
              <span className={styles.stepNo}>{index + 1}</span>
              {label}
            </li>
          ))}
        </ol>
      }
    >
      {step === 0 && (
        <form id={formId} className={styles.connect} onSubmit={submitConnect}>
          <p className={styles.intro}>
            StreamZH reads your indexers from Prowlarr and adds them here, with their priority, limits and VIP date. Nothing changes in Prowlarr.
          </p>
          <label className={ui.field}>
            <span className={styles.fieldLabel}>Prowlarr URL</span>
            <input
              className={ui.input}
              type="url"
              inputMode="url"
              autoComplete="off"
              spellCheck={false}
              placeholder="http://localhost:9696"
              value={connection.url}
              onChange={(e) => setConnection((c) => ({ ...c, url: e.target.value }))}
              autoFocus
            />
            <small>The address you open Prowlarr with, as seen from the StreamZH server.</small>
            {fieldError('url') && <small className={styles.fieldError}>{fieldError('url')}</small>}
          </label>
          <label className={ui.field}>
            <span className={styles.fieldLabel}>API key</span>
            <input
              className={`${ui.input} ${ui.mono}`}
              type="password"
              autoComplete="off"
              spellCheck={false}
              value={connection.apiKey}
              onChange={(e) => setConnection((c) => ({ ...c, apiKey: e.target.value }))}
            />
            <small>
              In Prowlarr: <strong>Settings → General → API Key</strong>.
            </small>
            {fieldError('apiKey') && <small className={styles.fieldError}>{fieldError('apiKey')}</small>}
          </label>
          {preview.error && Object.keys(connectErrors).length === 0 && <p className={ui.error}>{preview.error.message}</p>}
        </form>
      )}

      {step === 1 && (
        <div className={styles.choose}>
          <p className={styles.intro}>
            Found <strong>{items.length}</strong> indexer{items.length === 1 ? '' : 's'} in Prowlarr at <span className={ui.mono}>{hostOf(connection.url)}</span>
            , <strong>{choosable.length}</strong> can be imported.
          </p>
          {modes.size > 0 && (
            <ul className={styles.modes}>
              {(['direct', 'proxy'] as const)
                .filter((m) => modes.has(m))
                .map((m) => (
                  <li key={m}>
                    <Pill tone={modeText[m].tone}>{modeText[m].label}</Pill>
                    <span>{modeText[m].help}</span>
                  </li>
                ))}
            </ul>
          )}

          {items.length === 0 ? (
            <p className={ui.muted}>Prowlarr has no indexers.</p>
          ) : (
            <table className={styles.table}>
              <thead>
                <tr>
                  <th className={styles.checkCell}>
                    <input
                      type="checkbox"
                      aria-label="Select all"
                      checked={allChosen}
                      ref={(el) => {
                        if (el) el.indeterminate = !allChosen && selected.size > 0
                      }}
                      onChange={(e) => setSelected(new Set(e.target.checked ? choosable.map((i) => i.prowlarrId) : []))}
                      disabled={choosable.length === 0}
                    />
                  </th>
                  <th>Name</th>
                  <th>Protocol</th>
                  <th>Mode</th>
                  <th>Address</th>
                  <th>Own API key</th>
                  <th>Note</th>
                </tr>
              </thead>
              <tbody>
                {items.map((item) => {
                  const enabled = selectable(item)
                  const checkId = `${formId}-p${item.prowlarrId}`
                  return (
                    <tr key={item.prowlarrId} className={enabled ? undefined : styles.disabledRow}>
                      <td className={styles.checkCell}>
                        <input
                          id={checkId}
                          type="checkbox"
                          checked={selected.has(item.prowlarrId)}
                          disabled={!enabled}
                          onChange={(e) => toggle(item.prowlarrId, e.target.checked)}
                        />
                      </td>
                      <td className={styles.nameCell}>
                        <label htmlFor={checkId}>{item.name}</label>
                        <span className={styles.impl}>{item.implementation}</span>
                      </td>
                      <td data-label="Protocol">
                        <ProtocolChip protocol={item.protocol} />
                      </td>
                      <td data-label="Mode">{enabled ? <Pill tone={modeText[modeOf(item)].tone}>{modeText[modeOf(item)].label}</Pill> : '–'}</td>
                      <td data-label="Address" className={styles.address}>
                        {modeOf(item) === 'direct' && item.ownUrl
                          ? hostOf(item.ownUrl)
                          : item.baseUrl
                            ? `${hostOf(item.baseUrl)}${item.apiPath && item.apiPath !== '/api' ? item.apiPath : ''}`
                            : '–'}
                      </td>
                      <td data-label="Own API key">
                        {enabled && item.mode === 'proxy' && item.ownUrl ? (
                          <input
                            className={`${ui.input} ${styles.keyInput}`}
                            type="password"
                            autoComplete="off"
                            aria-label={`Own API key for ${item.name}`}
                            placeholder={`Optional, from ${hostOf(item.ownUrl)}`}
                            value={ownKeys[item.prowlarrId] ?? ''}
                            onChange={(e) => setOwnKeys((k) => ({ ...k, [item.prowlarrId]: e.target.value }))}
                          />
                        ) : (
                          '–'
                        )}
                      </td>
                      <td data-label="Note" className={styles.note}>
                        {!enabled ? (
                          <span className={styles.reason}>{item.reason ?? 'Not supported.'}</span>
                        ) : item.alreadyExists ? (
                          <span className={styles.exists}>Already in StreamZH, skipped.</span>
                        ) : (
                          <span className={ui.muted}>New</span>
                        )}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          )}
          {importer.error && <p className={ui.error}>{importer.error.message}</p>}
        </div>
      )}

      {step === 2 && importer.data && (
        <div className={styles.result}>
          <p className={styles.headline}>
            {importer.data.imported.length > 0 ? (
              <>
                Imported <strong>{importer.data.imported.length}</strong> indexer{importer.data.imported.length === 1 ? '' : 's'}.
              </>
            ) : (
              'Nothing was imported.'
            )}
          </p>
          {importer.data.imported.length > 0 && (
            <ul className={styles.resultList}>
              {importer.data.imported.map((i) => (
                <li key={i.id}>
                  <span className={styles.okMark} aria-hidden="true">
                    ✓
                  </span>
                  <span className={styles.resultName}>{i.name}</span>
                  <Pill tone={modeText[i.mode].tone}>{modeText[i.mode].label}</Pill>
                </li>
              ))}
            </ul>
          )}
          {importer.data.skipped.length > 0 && (
            <>
              <h3 className={styles.subhead}>Skipped</h3>
              <ul className={styles.resultList}>
                {importer.data.skipped.map((s) => (
                  <li key={s.prowlarrId}>
                    <span className={styles.skipMark} aria-hidden="true">
                      –
                    </span>
                    <span className={styles.resultName}>{s.name}</span>
                    <span className={styles.reason}>{s.reason}</span>
                  </li>
                ))}
              </ul>
            </>
          )}
          {importer.data.imported.length > 0 && (
            <p className={ui.muted}>Test them once to load their categories and search capabilities.</p>
          )}
        </div>
      )}
    </Dialog>
  )
}
