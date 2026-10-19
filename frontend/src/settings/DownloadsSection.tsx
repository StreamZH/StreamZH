import { useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import {
  downloadSettingsApi,
  downloadSettingsKeys,
  type DownloadClient,
  type DownloadSettings,
  type DuplicateAction,
  type EncryptedAction,
  type QueueSort,
  type ToolPriority,
  type UnwantedAction,
} from '../api/downloadSettings'
import FolderPicker from '../components/FolderPicker'
import ui from '../components/ui.module.css'
import { useAdvanced } from '../lib/advancedMode'
import styles from './DownloadsPage.module.css'
import { useIsOwner } from './useIsOwner'
import { NumberInput, Row, SaveBar, Section, Select, Toggle } from './SettingsKit'
import { useSettingsForm, type SettingsForm } from './useSettingsForm'

// Settings → Downloads, the engine part: one card per SABnzbd config page (folders, queue switches, post-processing
// switches, history, special). Every card saves only its own fields.

type Fields<K extends keyof DownloadSettings> = Pick<DownloadSettings, K>

function pick<K extends keyof DownloadSettings>(settings: DownloadSettings, fields: readonly K[]): Fields<K> {
  const result = {} as Fields<K>
  for (const field of fields) result[field] = settings[field]
  return result
}

/** A settings form for some fields of the download settings; saving sends only those and refreshes the shared copy. */
function useDownloadSection<K extends keyof DownloadSettings>(name: string, fields: readonly K[]): SettingsForm<Fields<K>> {
  const queryClient = useQueryClient()
  return useSettingsForm<Fields<K>>({
    queryKey: [...downloadSettingsKeys.settings, 'section', name],
    load: async () => pick(await queryClient.fetchQuery({ queryKey: downloadSettingsKeys.settings, queryFn: downloadSettingsApi.get, staleTime: 2000 }), fields),
    save: async (draft) => {
      const saved = await downloadSettingsApi.save(draft)
      queryClient.setQueryData(downloadSettingsKeys.settings, saved)
      return pick(saved, fields)
    },
  })
}

/** Folder path with Browse (server-side picker), plus Default or Off when it is not the default. */
function FolderInput({
  id,
  value,
  onChange,
  resetLabel,
  showReset,
  placeholder,
}: {
  id: string
  value: string
  onChange: (value: string) => void
  /** "Default" or "Off". */
  resetLabel: string
  showReset: boolean
  placeholder?: string
}) {
  const [picking, setPicking] = useState(false)
  return (
    <div className={styles.folder}>
      <input id={id} className={`${ui.input} ${ui.mono}`} value={value} placeholder={placeholder} spellCheck={false} onChange={(e) => onChange(e.target.value)} />
      <button className={ui.button} type="button" onClick={() => setPicking(true)}>
        Browse…
      </button>
      {showReset && (
        <button className={ui.button} type="button" onClick={() => onChange('')}>
          {resetLabel}
        </button>
      )}
      {picking && (
        <FolderPicker
          initialPath={value}
          onClose={() => setPicking(false)}
          onSelect={(selected) => {
            onChange(selected)
            setPicking(false)
          }}
        />
      )}
    </div>
  )
}

function Bar<T extends object>({ form, disabled }: { form: SettingsForm<T>; disabled?: boolean }) {
  return <SaveBar dirty={form.dirty} saving={form.saving} saved={form.saved} error={form.formError} onSave={form.save} onReset={form.reset} disabled={disabled} />
}

const clientOptions: { value: DownloadClient; label: string }[] = [
  { value: 'builtin', label: 'StreamZH (built-in Usenet downloader)' },
  { value: 'blackhole', label: 'Blackhole folder (SABnzbd, NZBGet …)' },
]

/** Which client gets grabbed releases, the blackhole folder, and the speed limit. */
export function DownloadClientSection({ info }: { info: DownloadSettings }) {
  const form = useDownloadSection('client', ['client', 'nzbFolder', 'speedLimitKBps'] as const)
  const v = form.values
  if (!v) return null
  const builtIn = v.client === 'builtin'
  return (
    <Section
      id="client"
      title="Download client"
      description={
        builtIn
          ? 'StreamZH downloads grabbed releases from your Usenet servers, repairs and unpacks them.'
          : 'Grabbed releases are saved as NZB files for another downloader.'
      }
      footer={<Bar form={form} />}
    >
      <Row label="Download client" htmlFor="dl-client" error={form.error('client')}>
        <Select id="dl-client" value={v.client} options={clientOptions} onChange={(client) => form.set({ client })} />
      </Row>
      {builtIn ? (
        <Row
          label="Speed limit"
          htmlFor="dl-speed"
          help="0 = unlimited. Scheduler rules can change it at set times."
          error={form.error('speedLimitKBps')}
        >
          <NumberInput id="dl-speed" value={v.speedLimitKBps} min={0} step={100} unit="KB/s" onChange={(n) => form.set({ speedLimitKBps: n ?? 0 })} />
        </Row>
      ) : (
        <Row
          label="NZB folder"
          htmlFor="dl-nzb"
          help={
            <>
              Let SABnzbd or NZBGet watch this folder. Passwords go into the file name as <code>title{'{{password}}'}.nzb</code>.
            </>
          }
          error={form.error('nzbFolder') ?? (form.dirty ? undefined : (info.problem ?? undefined))}
        >
          <FolderInput
            id="dl-nzb"
            value={v.nzbFolder}
            onChange={(nzbFolder) => form.set({ nzbFolder })}
            resetLabel="Default"
            showReset={v.nzbFolder !== '' && v.nzbFolder !== info.defaultNzbFolder}
          />
        </Row>
      )}
    </Section>
  )
}

/** SABnzbd's Folders page: work folders, watched folder, NZB backup. */
export function FoldersSection({ info }: { info: DownloadSettings }) {
  const form = useDownloadSection('folders', ['incompleteFolder', 'completeFolder', 'watchFolder', 'watchIntervalSeconds', 'nzbBackupFolder'] as const)
  const advanced = useAdvanced()
  const v = form.values
  if (!v) return null
  const problem = (field: 'incompleteFolder' | 'completeFolder' | 'watchFolder' | 'nzbBackupFolder', current: string | null) =>
    form.error(field) ?? (form.dirty ? undefined : (current ?? undefined))
  return (
    <Section
      id="folders"
      title="Folders"
      description="Downloads are assembled in the incomplete folder and moved to the completed folder, one subfolder per category."
      hiddenCount={advanced ? 0 : 4}
      footer={<Bar form={form} />}
    >
      <Row
        label="Watched folder"
        htmlFor="dl-watch"
        help="NZB files dropped here are downloaded and removed. A subfolder named after a category (movies/, series/ …) gives that category. Empty = off."
        error={problem('watchFolder', info.watchProblem)}
      >
        <FolderInput id="dl-watch" value={v.watchFolder} onChange={(watchFolder) => form.set({ watchFolder })} resetLabel="Off" showReset={v.watchFolder !== ''} placeholder="Off" />
      </Row>
      <Row label="Scan interval" htmlFor="dl-watch-interval" advanced help="How often the watched folder is checked." error={form.error('watchIntervalSeconds')}>
        <NumberInput id="dl-watch-interval" value={v.watchIntervalSeconds} min={1} max={3600} unit="seconds" onChange={(n) => form.set({ watchIntervalSeconds: n ?? 0 })} />
      </Row>
      <Row label="Incomplete folder" htmlFor="dl-incomplete" advanced help="Where downloads are assembled." error={problem('incompleteFolder', info.incompleteProblem)}>
        <FolderInput
          id="dl-incomplete"
          value={v.incompleteFolder}
          onChange={(incompleteFolder) => form.set({ incompleteFolder })}
          resetLabel="Default"
          showReset={v.incompleteFolder !== '' && v.incompleteFolder !== info.defaultIncompleteFolder}
        />
      </Row>
      <Row
        label="Completed folder"
        htmlFor="dl-complete"
        advanced
        help="Finished downloads go into a subfolder per category; categories can point elsewhere."
        error={problem('completeFolder', info.completeProblem)}
      >
        <FolderInput
          id="dl-complete"
          value={v.completeFolder}
          onChange={(completeFolder) => form.set({ completeFolder })}
          resetLabel="Default"
          showReset={v.completeFolder !== '' && v.completeFolder !== info.defaultCompleteFolder}
        />
      </Row>
      <Row
        label="NZB backup folder"
        htmlFor="dl-backup"
        advanced
        help="A copy of every added NZB is kept here. Empty = off."
        error={problem('nzbBackupFolder', info.nzbBackupProblem)}
      >
        <FolderInput id="dl-backup" value={v.nzbBackupFolder} onChange={(nzbBackupFolder) => form.set({ nzbBackupFolder })} resetLabel="Off" showReset={v.nzbBackupFolder !== ''} placeholder="Off" />
      </Row>
    </Section>
  )
}

const duplicateOptions: { value: DuplicateAction; label: string }[] = [
  { value: 'off', label: 'Off' },
  { value: 'tag', label: 'Tag the download' },
  { value: 'pause', label: 'Pause the download' },
  { value: 'fail', label: 'Fail (move to the history)' },
]

const sortOptions: { value: QueueSort; label: string }[] = [
  { value: 'off', label: 'Off (in the order added)' },
  { value: 'oldest', label: 'Age, oldest post first' },
  { value: 'newest', label: 'Age, newest post first' },
  { value: 'smallest', label: 'Size, smallest first' },
  { value: 'largest', label: 'Size, largest first' },
  { value: 'name', label: 'Name' },
]

const unwantedOptions: { value: UnwantedAction; label: string }[] = [
  { value: 'off', label: 'Off' },
  { value: 'warn', label: 'Warn' },
  { value: 'pause', label: 'Pause' },
  { value: 'fail', label: 'Fail' },
]

const encryptedOptions: { value: EncryptedAction; label: string }[] = [
  { value: 'off', label: 'Off (download it anyway)' },
  { value: 'pause', label: 'Pause' },
  { value: 'fail', label: 'Fail' },
]

/** SABnzbd's Switches → Queue. */
export function QueueSection() {
  const form = useDownloadSection('queue', [
    'duplicateAction',
    'pauseOnStart',
    'preCheck',
    'maxActiveDownloads',
    'propagationDelayMinutes',
    'autoSort',
    'unwantedExtensions',
    'unwantedAction',
    'encryptedAction',
    'downloadAllPar2',
    'minFreeSpaceGb',
  ] as const)
  const advanced = useAdvanced()
  const v = form.values
  if (!v) return null
  return (
    <Section id="queue" title="Queue" description="What happens to new downloads and in which order they run." hiddenCount={advanced ? 0 : 10} footer={<Bar form={form} />}>
      <Row
        label="Duplicate detection"
        htmlFor="dl-duplicates"
        help="A release with the same name or the same NZB in the queue or the history. Failed downloads do not count."
        error={form.error('duplicateAction')}
      >
        <Select id="dl-duplicates" value={v.duplicateAction} options={duplicateOptions} onChange={(duplicateAction) => form.set({ duplicateAction })} />
      </Row>
      <Row label="Pause downloading on start" advanced help="The queue starts paused every time StreamZH starts.">
        <Toggle checked={v.pauseOnStart} onChange={(pauseOnStart) => form.set({ pauseOnStart })} />
      </Row>
      <Row
        label="Check before download"
        advanced
        help="Asks the servers (STAT) whether every article exists first, and fails the download right away when par2 could not repair what is missing."
      >
        <Toggle checked={v.preCheck} onChange={(preCheck) => form.set({ preCheck })} />
      </Row>
      <Row
        label="Active downloads"
        htmlFor="dl-max-active"
        advanced
        help="How many downloads get articles at the same time. 1 = only the top of the queue, like SABnzbd. 0 = the next starts as soon as the one before has nothing left to hand out."
        error={form.error('maxActiveDownloads')}
      >
        <NumberInput id="dl-max-active" value={v.maxActiveDownloads} min={0} max={100} onChange={(n) => form.set({ maxActiveDownloads: n ?? 0 })} />
      </Row>
      <Row
        label="Propagation delay"
        htmlFor="dl-propagation"
        advanced
        help="Waits until a release was posted this long ago, so all servers have it. 0 = off."
        error={form.error('propagationDelayMinutes')}
      >
        <NumberInput id="dl-propagation" value={v.propagationDelayMinutes} min={0} max={1440} unit="minutes" onChange={(n) => form.set({ propagationDelayMinutes: n ?? 0 })} />
      </Row>
      <Row label="Sort the queue" htmlFor="dl-sort" advanced help="New downloads are sorted in by this; the priority still comes first." error={form.error('autoSort')}>
        <Select id="dl-sort" value={v.autoSort} options={sortOptions} onChange={(autoSort) => form.set({ autoSort })} />
      </Row>
      <Row
        label="Unwanted extensions"
        htmlFor="dl-unwanted"
        advanced
        help="Checked in the NZB and after unpacking. Executables are never moved to the completed folder anyway."
        error={form.error('unwantedExtensions') ?? form.error('unwantedAction')}
      >
        <div className={styles.inline}>
          <Select id="dl-unwanted-action" value={v.unwantedAction} options={unwantedOptions} onChange={(unwantedAction) => form.set({ unwantedAction })} />
        </div>
        <input
          id="dl-unwanted"
          className={`${ui.input} ${ui.mono} ${styles.wide}`}
          value={v.unwantedExtensions}
          spellCheck={false}
          onChange={(e) => form.set({ unwantedExtensions: e.target.value })}
        />
      </Row>
      <Row
        label="Password-protected RAR"
        htmlFor="dl-encrypted"
        advanced
        help="Noticed from the first article of a RAR volume when no password is known."
        error={form.error('encryptedAction')}
      >
        <Select id="dl-encrypted" value={v.encryptedAction} options={encryptedOptions} onChange={(encryptedAction) => form.set({ encryptedAction })} />
      </Row>
      <Row label="Download all par2 files" advanced help="Fetches every par2 recovery file right away. Off: only as many as a repair needs.">
        <Toggle checked={v.downloadAllPar2} onChange={(downloadAllPar2) => form.set({ downloadAllPar2 })} />
      </Row>
      <Row
        label="Minimum free space"
        htmlFor="dl-free"
        advanced
        help="Downloads pause when less is free in the incomplete folder and continue with 50 % more. 0 never pauses."
        error={form.error('minFreeSpaceGb')}
      >
        <NumberInput id="dl-free" value={v.minFreeSpaceGb} min={0} step={0.5} unit="GB" onChange={(n) => form.set({ minFreeSpaceGb: n ?? 0 })} />
      </Row>
    </Section>
  )
}

/** SABnzbd's Switches → Post processing. */
export function PostProcessingSection() {
  const form = useDownloadSection('post-processing', [
    'ignoreSamples',
    'deobfuscate',
    'abortHopeless',
    'onlyVerified',
    'recursiveUnpack',
    'cleanupList',
    'temporaryFolderName',
    'keepFiles',
    'postProcessWorkers',
  ] as const)
  const advanced = useAdvanced()
  const v = form.values
  if (!v) return null
  return (
    <Section
      id="post-processing"
      title="Post-processing"
      description="Repair with par2, unpack with 7-Zip, clean up. How much a download gets is set per category."
      hiddenCount={advanced ? 0 : 7}
      footer={<Bar form={form} />}
    >
      <Row label="Ignore samples" help="Sample files are not moved to the completed folder.">
        <Toggle checked={v.ignoreSamples} onChange={(ignoreSamples) => form.set({ ignoreSamples })} />
      </Row>
      <Row label="Deobfuscate final file names" help="A main file with a random name (a8f3k2…) gets the name of the download, together with its subtitles.">
        <Toggle checked={v.deobfuscate} onChange={(deobfuscate) => form.set({ deobfuscate })} />
      </Row>
      <Row label="Abort hopeless downloads" advanced help="A download fails as soon as more is missing than par2 can repair, so another release can be tried.">
        <Toggle checked={v.abortHopeless} onChange={(abortHopeless) => form.set({ abortHopeless })} />
      </Row>
      <Row label="Only post-process verified downloads" advanced help="Off: downloads par2 cannot verify or repair are unpacked and completed anyway, with a warning.">
        <Toggle checked={v.onlyVerified} onChange={(onlyVerified) => form.set({ onlyVerified })} />
      </Row>
      <Row label="Recursive unpacking" advanced help="Archives found inside archives are unpacked too (up to three levels).">
        <Toggle checked={v.recursiveUnpack} onChange={(recursiveUnpack) => form.set({ recursiveUnpack })} />
      </Row>
      <Row label="Cleanup list" htmlFor="dl-cleanup" advanced help="Files with these extensions are deleted after unpacking instead of being moved." error={form.error('cleanupList')}>
        <input
          id="dl-cleanup"
          className={`${ui.input} ${ui.mono} ${styles.wide}`}
          value={v.cleanupList}
          spellCheck={false}
          onChange={(e) => form.set({ cleanupList: e.target.value })}
        />
      </Row>
      <Row label="Temporary folder name" advanced help='Post-processing writes into "_UNPACK_name" and renames the folder when done, so other programs do not pick up half-finished folders.'>
        <Toggle checked={v.temporaryFolderName} onChange={(temporaryFolderName) => form.set({ temporaryFolderName })} />
      </Row>
      <Row label="Keep par2 and archive files" advanced help="After a successful unpack the download folder stays in the incomplete folder. You clean it up yourself.">
        <Toggle checked={v.keepFiles} onChange={(keepFiles) => form.set({ keepFiles })} />
      </Row>
      <Row label="Post-processing workers" htmlFor="dl-workers" advanced help="Downloads repaired and unpacked at the same time." error={form.error('postProcessWorkers')}>
        <NumberInput id="dl-workers" value={v.postProcessWorkers} min={1} max={4} onChange={(n) => form.set({ postProcessWorkers: n ?? 0 })} />
      </Row>
    </Section>
  )
}

/** Download history retention (SABnzbd's "History retention"). */
export function HistorySection() {
  const form = useDownloadSection('history', ['historyKeepDays', 'historyKeepItems', 'historyKeepFailed'] as const)
  const advanced = useAdvanced()
  const v = form.values
  if (!v) return null
  return (
    <Section
      id="history"
      title="History"
      description="Finished downloads in Activity → History. The cleanup runs every hour (System → Tasks)."
      hiddenCount={advanced ? 0 : 2}
      footer={<Bar form={form} />}
    >
      <Row label="Keep for" htmlFor="dl-history-days" help="0 = keep them all." error={form.error('historyKeepDays')}>
        <NumberInput id="dl-history-days" value={v.historyKeepDays} min={0} max={3650} unit="days" onChange={(n) => form.set({ historyKeepDays: n ?? 0 })} />
      </Row>
      <Row label="Keep at most" htmlFor="dl-history-items" advanced help="The newest ones stay. 0 = no limit." error={form.error('historyKeepItems')}>
        <NumberInput id="dl-history-items" value={v.historyKeepItems} min={0} max={100000} unit="downloads" wide onChange={(n) => form.set({ historyKeepItems: n ?? 0 })} />
      </Row>
      <Row label="Keep failed downloads" advanced help="Failed downloads stay until you remove them, so they can be retried. Off: they are cleaned up too, with their files.">
        <Toggle checked={v.historyKeepFailed} onChange={(historyKeepFailed) => form.set({ historyKeepFailed })} />
      </Row>
    </Section>
  )
}

const priorityOptions: { value: ToolPriority; label: string }[] = [
  { value: 'normal', label: 'Normal' },
  { value: 'belowNormal', label: 'Below normal' },
  { value: 'idle', label: 'Idle (only when nothing else runs)' },
]

/** SABnzbd's Special page: rarely needed knobs. Only in advanced mode. */
export function SpecialSection() {
  const form = useDownloadSection('special', ['articleTries', 'maxLineSpeedKBps', 'toolPriority', 'par2Arguments', 'sevenZipArguments'] as const)
  const owner = useIsOwner()
  const v = form.values
  if (!v) return null
  return (
    <Section id="special" title="Special" advanced description="Fine-tuning for the engine and the tools." footer={<Bar form={form} />}>
      <Row
        label="Tries per article"
        htmlFor="dl-tries"
        advanced
        help="How often an article is asked for again on one server after a timeout or a dropped connection, before the next server."
        error={form.error('articleTries')}
      >
        <NumberInput id="dl-tries" value={v.articleTries} min={1} max={10} onChange={(n) => form.set({ articleTries: n ?? 0 })} />
      </Row>
      <Row
        label="Maximum line speed"
        htmlFor="dl-line"
        advanced
        help="The speed of your internet line, for scheduler rules with a speed limit in percent. 0 = not set."
        error={form.error('maxLineSpeedKBps')}
      >
        <NumberInput id="dl-line" value={v.maxLineSpeedKBps} min={0} step={1000} unit="KB/s" wide onChange={(n) => form.set({ maxLineSpeedKBps: n ?? 0 })} />
      </Row>
      <Row label="par2 and 7-Zip priority" htmlFor="dl-priority" advanced help="Process priority of the tools, so repairing and unpacking do not slow down the rest." error={form.error('toolPriority')}>
        <Select id="dl-priority" value={v.toolPriority} options={priorityOptions} onChange={(toolPriority) => form.set({ toolPriority })} />
      </Row>
      <Row
        label="par2 parameters"
        htmlFor="dl-par2-args"
        advanced
        help={<>Extra options for repairs: <code>-t</code> (threads), <code>-T</code>, <code>-m</code> (memory MB), <code>-N</code>, <code>-S</code>, <code>-q</code>, <code>-v</code>. {owner ? '' : 'Only the owner can change this.'}</>}
        error={form.error('par2Arguments')}
      >
        <input
          id="dl-par2-args"
          className={`${ui.input} ${ui.mono} ${styles.wide}`}
          value={v.par2Arguments}
          spellCheck={false}
          disabled={!owner}
          placeholder="-t4 -m1024"
          onChange={(e) => form.set({ par2Arguments: e.target.value })}
        />
      </Row>
      <Row
        label="7-Zip parameters"
        htmlFor="dl-7z-args"
        advanced
        help={<>Extra options for unpacking: <code>-mmt</code> (threads), <code>-bb0</code>–<code>-bb3</code>, <code>-scc</code>, <code>-mcp=</code>, <code>-ao</code>. Output folder and password are set by StreamZH. {owner ? '' : 'Only the owner can change this.'}</>}
        error={form.error('sevenZipArguments')}
      >
        <input
          id="dl-7z-args"
          className={`${ui.input} ${ui.mono} ${styles.wide}`}
          value={v.sevenZipArguments}
          spellCheck={false}
          disabled={!owner}
          placeholder="-mmt4"
          onChange={(e) => form.set({ sevenZipArguments: e.target.value })}
        />
      </Row>
    </Section>
  )
}
