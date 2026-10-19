import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type ReactNode } from 'react'
import { ApiError } from '../api/client'
import { toolKeys, toolsApi, type ToolName, type ToolSettings, type ToolSource } from '../api/tools'
import ui from '../components/ui.module.css'
import { Pill } from '../indexers/Chips'
import { useAdvanced } from '../lib/advancedMode'
import { OwnerOnlyNote, ReadOnly } from '../settings/OwnerOnly'
import { useIsOwner } from '../settings/useIsOwner'
import { Row, SaveBar, Section, Toggle } from '../settings/SettingsKit'
import { percent } from '../status/format'
import styles from './Settings.module.css'

/** One tool as the page shows it. */
interface Tool {
  name: ToolName
  label: string
  purpose: string
  path: string
  found: boolean
  version: string | null
  resolved: string | null
  source: ToolSource
  problem: string | null
  installable: boolean
  installing: boolean
  progress: number | null
  error: string | null
  installedVersion: string | null
  installVersion: string | null
}

function toolsOf(data: ToolSettings): Tool[] {
  return [
    {
      name: 'par2',
      label: 'par2',
      purpose: 'Repairs damaged downloads.',
      path: data.par2Path,
      found: data.par2Found,
      version: data.par2Version,
      resolved: data.par2ResolvedPath,
      source: data.par2Source,
      problem: data.par2Problem,
      installable: data.par2Installable,
      installing: data.par2Installing,
      progress: data.par2InstallProgress,
      error: data.par2InstallError,
      installedVersion: data.par2InstalledVersion,
      installVersion: data.par2InstallVersion,
    },
    {
      name: 'sevenZip',
      label: '7-Zip',
      purpose: 'Unpacks RAR, 7z and ZIP archives.',
      path: data.sevenZipPath,
      found: data.sevenZipFound,
      version: data.sevenZipVersion,
      resolved: data.sevenZipResolvedPath,
      source: data.sevenZipSource,
      problem: data.sevenZipProblem,
      installable: data.sevenZipInstallable,
      installing: data.sevenZipInstalling,
      progress: data.sevenZipInstallProgress,
      error: data.sevenZipInstallError,
      installedVersion: data.sevenZipInstalledVersion,
      installVersion: data.sevenZipInstallVersion,
    },
    {
      name: 'ffprobe',
      label: 'ffprobe',
      purpose: 'Reads codecs, resolution, HDR and audio languages of video files (media info).',
      path: data.ffprobePath,
      found: data.ffprobeFound,
      version: data.ffprobeVersion,
      resolved: data.ffprobeResolvedPath,
      source: data.ffprobeSource,
      problem: data.ffprobeProblem,
      installable: data.ffprobeInstallable,
      installing: data.ffprobeInstalling,
      progress: data.ffprobeInstallProgress,
      error: data.ffprobeInstallError,
      installedVersion: data.ffprobeInstalledVersion,
      installVersion: data.ffprobeInstallVersion,
    },
  ]
}

const anyInstalling = (data: ToolSettings | undefined) => !!data && (data.par2Installing || data.sevenZipInstalling || data.ffprobeInstalling)

/**
 * Settings → Downloads → Tools: par2, 7-Zip and ffprobe. StreamZH installs them by itself when they are missing; the simple view
 * shows whether they are there and an Install button, the advanced view custom paths and the automatic installation.
 */
export function ToolsSection() {
  const queryClient = useQueryClient()
  const advanced = useAdvanced()
  const owner = useIsOwner()
  const [par2Path, setPar2Path] = useState<string | null>(null)
  const [sevenZipPath, setSevenZipPath] = useState<string | null>(null)
  const [ffprobePath, setFfprobePath] = useState<string | null>(null)
  const [autoInstall, setAutoInstall] = useState<boolean | null>(null)

  const install = useMutation({
    mutationFn: toolsApi.install,
    onMutate: () => {
      // The server marks the tool as installing right away: read it, so the progress shows while this request waits.
      window.setTimeout(() => {
        void queryClient.invalidateQueries({ queryKey: toolKeys.settings })
      }, 300)
    },
    onSuccess: (data) => {
      queryClient.setQueryData(toolKeys.settings, data)
    },
    onError: () => {
      void queryClient.invalidateQueries({ queryKey: toolKeys.settings })
    },
  })

  // Every second while an installation runs (started here or automatically at startup).
  const settings = useQuery({
    queryKey: toolKeys.settings,
    queryFn: toolsApi.settings,
    refetchInterval: (query) => (install.isPending || anyInstalling(query.state.data) ? 1000 : false),
  })

  const save = useMutation({
    mutationFn: toolsApi.save,
    onSuccess: (data) => {
      queryClient.setQueryData(toolKeys.settings, data)
      setPar2Path(null)
      setSevenZipPath(null)
      setFfprobePath(null)
      setAutoInstall(null)
    },
  })

  if (!settings.data) return null
  const data = settings.data
  const tools = toolsOf(data)
  const par2 = par2Path ?? data.par2Path
  const sevenZip = sevenZipPath ?? data.sevenZipPath
  const ffprobe = ffprobePath ?? data.ffprobePath
  const auto = autoInstall ?? data.autoInstall
  const dirty = par2.trim() !== data.par2Path || sevenZip.trim() !== data.sevenZipPath || ffprobe.trim() !== data.ffprobePath || auto !== data.autoInstall
  const fieldErrors = save.error instanceof ApiError ? save.error.fieldErrors : {}
  const manual = tools.filter((t) => !t.found && !t.installable)

  const fieldError = (field: string) => (save.error instanceof ApiError ? fieldErrors[field]?.[0] : undefined)
  const nameHelp = (names: string) => `Empty: use the copy StreamZH installs or finds. The file must be named ${names}.`

  return (
    <Section
      id="tools"
      title="Tools"
      description={`Finished downloads are repaired with par2 and unpacked with 7-Zip; ffprobe reads the media info of video files.${tools.some((t) => t.installable) ? ' StreamZH installs them by itself when they are missing.' : ''}`}
      hiddenCount={advanced ? 0 : 4}
      footer={
        advanced &&
        (!owner ? (
          <OwnerOnlyNote>Only the owner can change the tools: StreamZH runs these programs.</OwnerOnlyNote>
        ) : (
          <SaveBar
            dirty={dirty}
            saving={save.isPending}
            saved={save.isSuccess && !dirty}
            error={save.error && Object.keys(fieldErrors).length === 0 ? save.error.message : null}
            onSave={() => save.mutate({ par2Path: par2.trim(), sevenZipPath: sevenZip.trim(), ffprobePath: ffprobe.trim(), autoInstall: auto })}
            onReset={() => {
              setPar2Path(null)
              setSevenZipPath(null)
              setFfprobePath(null)
              setAutoInstall(null)
              save.reset()
            }}
          />
        ))
      }
    >
      <ul className={styles.toolList}>
        {tools.map((tool) => (
          <ToolRow
            key={tool.name}
            tool={tool}
            advanced={advanced}
            autoInstall={data.autoInstall}
            busy={install.isPending && install.variables === tool.name}
            requestError={install.variables === tool.name && install.error ? install.error.message : null}
            onInstall={() => install.mutate(tool.name)}
          />
        ))}
      </ul>
      {manual.length > 0 && <ManualHint settings={data} tools={manual} />}
      <ReadOnly locked={!owner}>
      <Row label="par2 path" htmlFor="tool-par2" advanced help={nameHelp('par2, par2.exe or par2j64.exe')} error={fieldError('par2Path')}>
        <input
          id="tool-par2"
          className={`${ui.input} ${ui.mono}`}
          value={par2}
          placeholder={data.os === 'Windows' ? 'C:\\Tools\\par2.exe' : '/usr/bin/par2'}
          onChange={(e) => setPar2Path(e.target.value)}
          spellCheck={false}
        />
      </Row>
      <Row label="7-Zip path" htmlFor="tool-7zip" advanced help={nameHelp('7z, 7za, 7zz or 7z.exe')} error={fieldError('sevenZipPath')}>
        <input
          id="tool-7zip"
          className={`${ui.input} ${ui.mono}`}
          value={sevenZip}
          placeholder={data.os === 'Windows' ? 'C:\\Program Files\\7-Zip\\7z.exe' : '/usr/bin/7zz'}
          onChange={(e) => setSevenZipPath(e.target.value)}
          spellCheck={false}
        />
      </Row>
      <Row label="ffprobe path" htmlFor="tool-ffprobe" advanced help={nameHelp('ffprobe or ffprobe.exe')} error={fieldError('ffprobePath')}>
        <input
          id="tool-ffprobe"
          className={`${ui.input} ${ui.mono}`}
          value={ffprobe}
          placeholder={data.os === 'Windows' ? 'C:\\ffmpeg\\bin\\ffprobe.exe' : '/usr/bin/ffprobe'}
          onChange={(e) => setFfprobePath(e.target.value)}
          spellCheck={false}
        />
      </Row>
      <Row
        label="Install automatically"
        advanced
        help={
          <>
            Missing tools are installed at startup into <code>{data.installFolder ?? 'the data folder'}</code>, from their official releases (checked by
            SHA-256).
          </>
        }
      >
        <Toggle checked={auto} onChange={setAutoInstall} />
      </Row>
      </ReadOnly>
    </Section>
  )
}

interface ToolRowProps {
  tool: Tool
  advanced: boolean
  autoInstall: boolean
  busy: boolean
  requestError: string | null
  onInstall: () => void
}

function ToolRow({ tool, advanced, autoInstall, busy, requestError, onInstall }: ToolRowProps) {
  const installing = tool.installing || busy
  const error = tool.found || installing ? null : (tool.error ?? requestError)
  const canInstall = tool.installable && (!tool.found || (advanced && tool.source === 'Installed'))

  return (
    <li className={styles.toolRow}>
      <div className={styles.toolMain}>
        <div className={styles.toolHead}>
          <strong>{tool.label}</strong>
          <StatusPill tool={tool} installing={installing} failed={error != null} />
        </div>
        <span>{tool.purpose}</span>
        <small>{where(tool, installing, autoInstall)}</small>
        {tool.problem && <small className={styles.error}>{tool.problem}</small>}
        {error && <small className={styles.error}>{error}</small>}
      </div>
      {canInstall && (
        <button type="button" className={`${tool.found ? ui.button : ui.primary} ${ui.sm}`} disabled={installing} onClick={onInstall}>
          {installing ? 'Installing…' : tool.found ? 'Reinstall' : 'Install'}
        </button>
      )}
    </li>
  )
}

function StatusPill({ tool, installing, failed }: { tool: Tool; installing: boolean; failed: boolean }) {
  if (installing) return <Pill tone="info">Installing… {tool.progress != null ? percent(tool.progress) : ''}</Pill>
  if (tool.found) return <Pill tone="ok">{tool.version ?? 'Found'}</Pill>
  if (failed) return <Pill tone="error">Install failed</Pill>
  return <Pill tone="warn">Not found</Pill>
}

/** Where the tool comes from, in plain words. */
function where(tool: Tool, installing: boolean, autoInstall: boolean): ReactNode {
  if (installing) return `Downloading ${tool.label} ${tool.installVersion ?? ''} from its official release.`
  if (!tool.found || !tool.resolved) {
    if (!tool.installable) return 'Install it yourself, see below.'
    return autoInstall ? 'StreamZH installs it at the next start, or now with Install.' : 'Press Install and StreamZH sets it up.'
  }

  switch (tool.source) {
    case 'Installed':
      return `Installed by StreamZH${tool.installedVersion ? `, ${tool.installedVersion}` : ''}`
    case 'Configured':
      return (
        <>
          Your path: <code>{tool.resolved}</code>
        </>
      )
    case 'Path':
      return (
        <>
          From PATH: <code>{tool.resolved}</code>
        </>
      )
    default:
      return <code>{tool.resolved}</code>
  }
}

/** Only for systems where StreamZH cannot install a tool by itself (e.g. 32-bit or FreeBSD). */
function ManualHint({ settings, tools }: { settings: ToolSettings; tools: Tool[] }) {
  const names = new Intl.ListFormat('en', { type: 'conjunction' }).format(tools.map((t) => t.label))
  if (settings.os === 'Windows') {
    return (
      <p className={styles.text}>
        Install {names} yourself:{' '}
        {tools.some((t) => t.name === 'sevenZip') && (
          <>
            7-Zip from{' '}
            <a href="https://www.7-zip.org/" target="_blank" rel="noreferrer">
              7-zip.org
            </a>
            {tools.length > 1 ? ', ' : '.'}
          </>
        )}
        {tools.some((t) => t.name === 'par2') && (
          <>
            par2 from the{' '}
            <a href="https://github.com/animetosho/par2cmdline-turbo/releases" target="_blank" rel="noreferrer">
              par2cmdline-turbo releases
            </a>{' '}
            (put the folder with <code>par2.exe</code> on PATH, or enter its path below){tools.some((t) => t.name === 'ffprobe') ? ', ' : '.'}
          </>
        )}
        {tools.some((t) => t.name === 'ffprobe') && (
          <>
            ffprobe from an FFmpeg build linked on{' '}
            <a href="https://ffmpeg.org/download.html" target="_blank" rel="noreferrer">
              ffmpeg.org
            </a>{' '}
            (put the folder with <code>ffprobe.exe</code> on PATH, or enter its path below).
          </>
        )}
      </p>
    )
  }

  const packages = (os: 'macOS' | 'Linux') =>
    tools
      .map((t) => (t.name === 'par2' ? 'par2' : t.name === 'sevenZip' ? (os === 'macOS' ? 'sevenzip' : '7zip') : 'ffmpeg'))
      .join(' ')
  return (
    <p className={styles.text}>
      Install {names} yourself, for example with{' '}
      <code>{settings.os === 'macOS' ? `brew install ${packages('macOS')}` : `sudo apt install ${packages('Linux')}`}</code>
      {tools.some((t) => t.name === 'ffprobe') && ' (ffprobe comes with ffmpeg)'}.
    </p>
  )
}
