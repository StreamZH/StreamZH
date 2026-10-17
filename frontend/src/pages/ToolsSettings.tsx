import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { ApiError } from '../api/client'
import { toolKeys, toolsApi, type ToolSettings, type ToolSource } from '../api/tools'
import ui from '../components/ui.module.css'
import page from './Page.module.css'
import styles from './Settings.module.css'

const sourceLabels: Record<ToolSource, string> = {
  NotFound: '',
  Configured: 'configured path',
  Bundled: 'StreamZH tools folder',
  Path: 'PATH',
  WindowsDefault: 'default install folder',
}

/** Settings → Downloads → Tools: where par2 and 7-Zip are and whether StreamZH can use them. */
export function ToolsSection() {
  const queryClient = useQueryClient()
  const settings = useQuery({ queryKey: toolKeys.settings, queryFn: toolsApi.settings })
  const [par2Path, setPar2Path] = useState<string | null>(null)
  const [sevenZipPath, setSevenZipPath] = useState<string | null>(null)

  const save = useMutation({
    mutationFn: toolsApi.save,
    onSuccess: (data) => {
      queryClient.setQueryData(toolKeys.settings, data)
      setPar2Path(null)
      setSevenZipPath(null)
    },
  })

  if (!settings.data) return null
  const data = settings.data
  const par2 = par2Path ?? data.par2Path
  const sevenZip = sevenZipPath ?? data.sevenZipPath
  const dirty = par2.trim() !== data.par2Path || sevenZip.trim() !== data.sevenZipPath
  const fieldErrors = save.error instanceof ApiError ? save.error.fieldErrors : {}

  const submit = (event: FormEvent) => {
    event.preventDefault()
    save.mutate({ par2Path: par2.trim(), sevenZipPath: sevenZip.trim() })
  }

  return (
    <form id="tools" className={page.panel} onSubmit={submit}>
      <h2 className={page.panelTitle}>Tools</h2>
      <p className={styles.text}>
        Downloads are repaired with par2 and unpacked with 7-Zip. Leave a path empty to find the tool automatically (StreamZH tools folder,
        then PATH{data.os === 'Windows' ? ', then C:\\Program Files\\7-Zip' : ''}).
      </p>
      <div className={styles.grid}>
        <ToolField
          label="par2"
          value={par2}
          onChange={setPar2Path}
          placeholder={data.os === 'Windows' ? 'C:\\Tools\\par2.exe' : '/usr/bin/par2'}
          found={data.par2Found}
          version={data.par2Version}
          resolved={data.par2ResolvedPath}
          source={data.par2Source}
          problem={data.par2Problem}
          error={fieldErrors.par2Path?.[0]}
        />
        <ToolField
          label="7-Zip"
          value={sevenZip}
          onChange={setSevenZipPath}
          placeholder={data.os === 'Windows' ? 'C:\\Program Files\\7-Zip\\7z.exe' : '/usr/bin/7z'}
          found={data.sevenZipFound}
          version={data.sevenZipVersion}
          resolved={data.sevenZipResolvedPath}
          source={data.sevenZipSource}
          problem={data.sevenZipProblem}
          error={fieldErrors.sevenZipPath?.[0]}
        />
      </div>
      {(!data.par2Found || !data.sevenZipFound) && <InstallHint settings={data} />}
      <div className={styles.actions}>
        <button className={styles.primary} type="submit" disabled={!dirty || save.isPending}>
          {save.isPending ? 'Saving…' : 'Save'}
        </button>
        {save.isSuccess && !dirty && <span className={styles.ok}>Saved</span>}
        {save.error && Object.keys(fieldErrors).length === 0 && <span className={styles.error}>{save.error.message}</span>}
      </div>
    </form>
  )
}

interface ToolFieldProps {
  label: string
  value: string
  onChange: (value: string) => void
  placeholder: string
  found: boolean
  version: string | null
  resolved: string | null
  source: ToolSource
  problem: string | null
  error: string | undefined
}

function ToolField({ label, value, onChange, placeholder, found, version, resolved, source, problem, error }: ToolFieldProps) {
  return (
    <label className={styles.field}>
      <span>
        {label}
        <span className={`${ui.badge} ${found ? ui.ok : ui.missing}`}>{found ? (version ?? 'found') : 'not found'}</span>
      </span>
      <input className={ui.mono} value={value} placeholder={placeholder} onChange={(e) => onChange(e.target.value)} spellCheck={false} />
      {found && resolved && (
        <small>
          Using <code>{resolved}</code> ({sourceLabels[source]})
        </small>
      )}
      {problem && <small className={styles.error}>{problem}</small>}
      {error && <small className={styles.error}>{error}</small>}
    </label>
  )
}

function InstallHint({ settings }: { settings: ToolSettings }) {
  if (settings.os === 'Windows') {
    return (
      <p className={styles.text}>
        {!settings.sevenZipFound && (
          <>
            Install 7-Zip from{' '}
            <a href="https://www.7-zip.org/" target="_blank" rel="noreferrer">
              7-zip.org
            </a>
            .{' '}
          </>
        )}
        {!settings.par2Found && (
          <>
            For par2, download par2cmdline from its{' '}
            <a href="https://github.com/Parchive/par2cmdline/releases" target="_blank" rel="noreferrer">
              GitHub releases
            </a>{' '}
            and put <code>par2.exe</code> into <code>{settings.bundledFolder}</code>.
          </>
        )}
      </p>
    )
  }

  return (
    <p className={styles.text}>
      Install with <code>{settings.os === 'macOS' ? 'brew install par2 sevenzip' : 'sudo apt install par2 p7zip-full'}</code>, or put the
      programs into <code>{settings.bundledFolder}</code>.
    </p>
  )
}
