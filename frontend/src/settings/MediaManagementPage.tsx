import { useQuery } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import {
  mediaManagementApi,
  mediaManagementKeys,
  type ColonReplacement,
  type EpisodeTitleRequired,
  type FileDateMode,
  type ImportMode,
  type MediaManagementSettings,
  type MultiEpisodeStyle,
  type ProperHandling,
  type RescanAfterRefresh,
} from '../api/mediaManagement'
import ui from '../components/ui.module.css'
import { setAdvanced } from '../lib/advancedMode'
import FormatField from '../mediaManagement/FormatField'
import mm from '../mediaManagement/MediaManagement.module.css'
import { RootFoldersSection } from './RootFoldersSection'
import { NumberInput, Row, SaveBar, Section, Select, SettingsPage, Toggle } from './SettingsKit'
import { useSettingsForm } from './useSettingsForm'

const colonOptions: { value: ColonReplacement; label: string }[] = [
  { value: 'smart', label: 'Smart replace (dash or space dash)' },
  { value: 'delete', label: 'Delete' },
  { value: 'dash', label: 'Replace with dash' },
  { value: 'spaceDash', label: 'Replace with space dash' },
  { value: 'spaceDashSpace', label: 'Replace with space dash space' },
]

const multiEpisodeOptions: { value: MultiEpisodeStyle; label: string }[] = [
  { value: 'extend', label: 'Extend (S01E01-02-03)' },
  { value: 'duplicate', label: 'Duplicate (S01E01.S01E02)' },
  { value: 'repeat', label: 'Repeat (S01E01E02E03)' },
  { value: 'scene', label: 'Scene (S01E01-E02-E03)' },
  { value: 'range', label: 'Range (S01E01-03)' },
  { value: 'prefixedRange', label: 'Prefixed range (S01E01-E03)' },
]

const importModeOptions: { value: ImportMode; label: string }[] = [
  { value: 'move', label: 'Move (default)' },
  { value: 'copy', label: 'Copy or hard link (keep the download)' },
]

const titleRequiredOptions: { value: EpisodeTitleRequired; label: string }[] = [
  { value: 'always', label: 'Always' },
  { value: 'bulkSeasonReleases', label: 'Only for season packs' },
  { value: 'never', label: 'Never' },
]

const properOptions: { value: ProperHandling; label: string }[] = [
  { value: 'preferAndUpgrade', label: 'Prefer and upgrade' },
  { value: 'doNotUpgrade', label: 'Do not upgrade automatically' },
  { value: 'doNotPrefer', label: 'Do not prefer' },
]

const rescanOptions: { value: RescanAfterRefresh; label: string }[] = [
  { value: 'always', label: 'Always' },
  { value: 'afterManualRefresh', label: 'After a manual refresh' },
  { value: 'never', label: 'Never' },
]

const fileDateOptions: { value: FileDateMode; label: string }[] = [
  { value: 'none', label: 'None' },
  { value: 'releaseDate', label: 'Release date (in cinemas / air date)' },
]

const namingKeys = [
  'renameMovies',
  'replaceIllegalCharacters',
  'colonReplacement',
  'standardMovieFormat',
  'movieFolderFormat',
  'renameEpisodes',
  'standardEpisodeFormat',
  'dailyEpisodeFormat',
  'animeEpisodeFormat',
  'seriesFolderFormat',
  'useSeasonFolders',
  'seasonFolderFormat',
  'specialsFolderFormat',
  'multiEpisodeStyle',
] as const

/** Waits until the value has not changed for a moment (for the naming preview while typing). */
function useDebounced<T>(value: T, ms: number): T {
  const [debounced, setDebounced] = useState(value)
  useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), ms)
    return () => clearTimeout(timer)
  }, [value, ms])
  return debounced
}

/**
 * Settings → Media Management (Radarr/Sonarr "Media Management"): root folders, how files and folders are named,
 * how finished downloads are imported, file handling, permissions and completed/failed download handling.
 */
export default function MediaManagementPage() {
  const form = useSettingsForm<MediaManagementSettings>({
    queryKey: mediaManagementKeys.settings,
    load: mediaManagementApi.settings,
    save: mediaManagementApi.save,
  })
  const values = form.values

  // A save error on a hidden expert field switches advanced mode on, so it can be fixed.
  const expertError = form.hasError(
    'dailyEpisodeFormat',
    'animeEpisodeFormat',
    'specialsFolderFormat',
    'minimumFreeSpaceMb',
    'recycleBinCleanupDays',
    'chmodFolder',
    'chownGroup',
  )
  useEffect(() => {
    if (expertError) setAdvanced(true)
  }, [expertError])

  const naming = values ? Object.fromEntries(namingKeys.map((k) => [k, values[k]])) : null
  const previewInput = useDebounced(naming, 300)
  const preview = useQuery({
    queryKey: mediaManagementKeys.preview(previewInput),
    queryFn: () => mediaManagementApi.preview({ ...(values as MediaManagementSettings), ...(previewInput as Partial<MediaManagementSettings>) }),
    enabled: previewInput != null,
    placeholderData: (previous) => previous,
    staleTime: 60_000,
  })
  const example = (field: keyof NonNullable<typeof preview.data>, error: string) =>
    preview.data?.errors[error] ? null : (preview.data?.[field] as string | null | undefined)
  const err = (field: string) => form.error(field) ?? preview.data?.errors[field]?.[0]

  const set = form.set
  const save = (
    <div className={`${mm.saveDock} ${form.dirty || form.saving || form.formError ? '' : mm.saveDockIdle}`}>
      <SaveBar dirty={form.dirty} saving={form.saving} saved={form.saved} error={form.formError} onSave={form.save} onReset={form.reset} />
    </div>
  )

  return (
    <SettingsPage title="Media Management" lead="Where your movies and series live, how files are named and how finished downloads are imported.">
      <RootFoldersSection windows={values?.isWindows ?? false} />

      {form.query.isError && <p className={ui.error}>{form.query.error.message}</p>}
      {values && (
        <>
          <Section
            id="movie-naming"
            title="Movie naming"
            description="Names for imported movie files and new movie folders. Existing files keep their names until you use Rename files on the movie."
            hiddenCount={1}
          >
            <Row label="Rename movies" help="Name imported files with the format below. Off: the file name from the download is kept.">
              <Toggle checked={values.renameMovies} onChange={(v) => set({ renameMovies: v })} />
            </Row>
            <Row label="Replace illegal characters" advanced
              help={'Replace characters Windows does not allow (\\ / ? * < > | ") instead of removing them, e.g. Face/Off → Face+Off.'}
            >
              <Toggle checked={values.replaceIllegalCharacters} onChange={(v) => set({ replaceIllegalCharacters: v })} />
            </Row>
            <Row label="Colon replacement" htmlFor="mm-colon" help="What a colon in a title becomes (colons are not allowed in Windows file names).">
              <Select id="mm-colon" value={values.colonReplacement} onChange={(v) => set({ colonReplacement: v })} options={colonOptions} />
            </Row>
            <Row label="Standard movie format" htmlFor="mm-movie-format" error={err('standardMovieFormat')}>
              <FormatField
                id="mm-movie-format"
                kind="movieFile"
                value={values.standardMovieFormat}
                onChange={(v) => set({ standardMovieFormat: v })}
                example={example('movieFile', 'standardMovieFormat')}
              />
            </Row>
            <Row label="Movie folder format" htmlFor="mm-movie-folder" error={err('movieFolderFormat')} help="Used for movies you add from now on.">
              <FormatField
                id="mm-movie-folder"
                kind="movieFolder"
                value={values.movieFolderFormat}
                onChange={(v) => set({ movieFolderFormat: v })}
                example={example('movieFolder', 'movieFolderFormat')}
              />
            </Row>
          </Section>

          <Section id="episode-naming" title="Episode naming" description="Names for imported episode files, series and season folders." hiddenCount={4}>
            <Row label="Rename episodes" help="Name imported files with the formats below. Off: the file name from the download is kept.">
              <Toggle checked={values.renameEpisodes} onChange={(v) => set({ renameEpisodes: v })} />
            </Row>
            <Row label="Standard episode format" htmlFor="mm-episode-format" error={err('standardEpisodeFormat')}>
              <FormatField
                id="mm-episode-format"
                kind="episodeFile"
                value={values.standardEpisodeFormat}
                onChange={(v) => set({ standardEpisodeFormat: v })}
                example={example('standardEpisode', 'standardEpisodeFormat')}
                exampleLabel="Single episode"
              />
              {example('multiEpisode', 'standardEpisodeFormat') && (
                <p className={mm.example}>
                  <span>Multi-episode</span>
                  <code>{example('multiEpisode', 'standardEpisodeFormat')}</code>
                </p>
              )}
            </Row>
            <Row
              label="Daily episode format"
              htmlFor="mm-daily-format"
              advanced
              error={err('dailyEpisodeFormat')}
              help="For releases named by air date (talk shows, news)."
            >
              <FormatField
                id="mm-daily-format"
                kind="episodeFile"
                value={values.dailyEpisodeFormat}
                onChange={(v) => set({ dailyEpisodeFormat: v })}
                example={example('dailyEpisode', 'dailyEpisodeFormat')}
              />
            </Row>
            <Row
              label="Anime episode format"
              htmlFor="mm-anime-format"
              advanced
              error={err('animeEpisodeFormat')}
              help="For Japanese animation (TMDB genre Animation, original language Japanese)."
            >
              <FormatField
                id="mm-anime-format"
                kind="episodeFile"
                value={values.animeEpisodeFormat}
                onChange={(v) => set({ animeEpisodeFormat: v })}
                example={example('animeEpisode', 'animeEpisodeFormat')}
              />
            </Row>
            <Row label="Series folder format" htmlFor="mm-series-folder" error={err('seriesFolderFormat')} help="Used for series you add from now on.">
              <FormatField
                id="mm-series-folder"
                kind="seriesFolder"
                value={values.seriesFolderFormat}
                onChange={(v) => set({ seriesFolderFormat: v })}
                example={example('seriesFolder', 'seriesFolderFormat')}
              />
            </Row>
            <Row label="Season folders" advanced help="Put episodes into a folder per season. Off: all episodes go straight into the series folder.">
              <Toggle checked={values.useSeasonFolders} onChange={(v) => set({ useSeasonFolders: v })} />
            </Row>
            {values.useSeasonFolders && (
              <>
                <Row label="Season folder format" htmlFor="mm-season-folder" error={err('seasonFolderFormat')}>
                  <FormatField
                    id="mm-season-folder"
                    kind="seasonFolder"
                    value={values.seasonFolderFormat}
                    onChange={(v) => set({ seasonFolderFormat: v })}
                    example={example('seasonFolder', 'seasonFolderFormat')}
                  />
                </Row>
                <Row label="Specials folder format" htmlFor="mm-specials-folder" advanced error={err('specialsFolderFormat')} help="Folder for season 0.">
                  <FormatField
                    id="mm-specials-folder"
                    kind="seasonFolder"
                    value={values.specialsFolderFormat}
                    onChange={(v) => set({ specialsFolderFormat: v })}
                    example={example('specialsFolder', 'specialsFolderFormat')}
                  />
                </Row>
              </>
            )}
            <Row label="Multi-episode style" htmlFor="mm-multi" help="How a file with several episodes is named.">
              <Select id="mm-multi" value={values.multiEpisodeStyle} onChange={(v) => set({ multiEpisodeStyle: v })} options={multiEpisodeOptions} />
            </Row>
          </Section>

          <Section
            id="importing"
            title="Importing"
            description="How finished downloads get into the library."
            hiddenCount={3 + (values.importMode === 'copy' ? 1 : 0) + (values.skipFreeSpaceCheck ? 0 : 1)}
          >
            <Row
              label="Import mode"
              htmlFor="mm-import-mode"
              advanced
              help="Move is right for Usenet. Copy keeps the download where it is, e.g. when another program still needs it."
            >
              <Select id="mm-import-mode" value={values.importMode} onChange={(v) => set({ importMode: v })} options={importModeOptions} />
            </Row>
            {values.importMode === 'copy' && (
              <Row label="Use hard links" advanced help="Link instead of copy when the download and the library are on the same drive: no extra space, done at once.">
                <Toggle checked={values.copyUsingHardlinks} onChange={(v) => set({ copyUsingHardlinks: v })} />
              </Row>
            )}
            <Row label="Skip free space check" advanced help="Import even if the library drive looks full (for drives that report free space wrongly).">
              <Toggle checked={values.skipFreeSpaceCheck} onChange={(v) => set({ skipFreeSpaceCheck: v })} />
            </Row>
            {!values.skipFreeSpaceCheck && (
              <Row label="Minimum free space" htmlFor="mm-free" advanced error={err('minimumFreeSpaceMb')} help="Imports stop when less than this would stay free.">
                <NumberInput
                  id="mm-free"
                  value={values.minimumFreeSpaceMb}
                  min={0}
                  step={50}
                  unit="MB"
                  onChange={(v) => set({ minimumFreeSpaceMb: v ?? 0 })}
                />
              </Row>
            )}
            <Row label="Import extra files" help="Also import subtitles and other files next to the video, renamed to match it.">
              <Toggle checked={values.importExtraFiles} onChange={(v) => set({ importExtraFiles: v })} />
            </Row>
            {values.importExtraFiles && (
              <Row
                label="Extra file extensions"
                htmlFor="mm-extras"
                error={err('extraFileExtensions')}
                help={
                  <>
                    Separated by commas, e.g. <code>srt,ass,sub,idx,nfo</code>. A scene .nfo is saved as .nfo-orig.
                  </>
                }
              >
                <input
                  id="mm-extras"
                  className={`${ui.input} ${ui.mono}`}
                  value={values.extraFileExtensions}
                  onChange={(e) => set({ extraFileExtensions: e.target.value })}
                  spellCheck={false}
                />
              </Row>
            )}
            <Row
              label="Episode title required"
              htmlFor="mm-title-required"
              advanced
              help="Wait up to 48 hours after the air date when the episode title is not known yet and the format uses it."
            >
              <Select id="mm-title-required" value={values.episodeTitleRequired} onChange={(v) => set({ episodeTitleRequired: v })} options={titleRequiredOptions} />
            </Row>
          </Section>

          <Section id="file-management" title="File management" hiddenCount={3 + (values.recycleBin.trim() !== '' ? 1 : 0)}>
            <Row label="Unmonitor deleted files" help="When a file disappears from disk, stop monitoring the movie or episode so it is not downloaded again.">
              <Toggle checked={values.unmonitorDeletedFiles} onChange={(v) => set({ unmonitorDeletedFiles: v })} />
            </Row>
            <Row
              label="Propers and repacks"
              htmlFor="mm-propers"
              advanced
              help="Prefer and upgrade: a PROPER or REPACK replaces the file. Do not upgrade: preferred when grabbing, but no replacement. Do not prefer: ignored."
            >
              <Select id="mm-propers" value={values.propersAndRepacks} onChange={(v) => set({ propersAndRepacks: v })} options={properOptions} />
            </Row>
            <Row label="Rescan folder after refresh" htmlFor="mm-rescan" advanced help="Read the folder again after the metadata of a movie or series was refreshed.">
              <Select id="mm-rescan" value={values.rescanAfterRefresh} onChange={(v) => set({ rescanAfterRefresh: v })} options={rescanOptions} />
            </Row>
            <Row label="Change file date" htmlFor="mm-file-date" advanced help="Set the date of imported files to the release or air date.">
              <Select id="mm-file-date" value={values.changeFileDate} onChange={(v) => set({ changeFileDate: v })} options={fileDateOptions} />
            </Row>
            <Row
              label="Recycling bin"
              htmlFor="mm-recycle"
              error={err('recycleBin')}
              help="Replaced and deleted files go here instead of being deleted. Empty: files are deleted for good."
            >
              <input
                id="mm-recycle"
                className={`${ui.input} ${ui.mono}`}
                value={values.recycleBin}
                placeholder={values.isWindows ? 'D:\\Recycle' : '/media/.recycle'}
                onChange={(e) => set({ recycleBin: e.target.value })}
                spellCheck={false}
              />
            </Row>
            {values.recycleBin.trim() !== '' && (
              <Row label="Recycling bin cleanup" htmlFor="mm-cleanup" advanced error={err('recycleBinCleanupDays')} help="Files older than this are deleted from the bin. 0: never.">
                <NumberInput
                  id="mm-cleanup"
                  value={values.recycleBinCleanupDays}
                  min={0}
                  max={3650}
                  unit="days"
                  onChange={(v) => set({ recycleBinCleanupDays: v ?? 0 })}
                />
              </Row>
            )}
          </Section>

          <Section id="folders" title="Folders" advanced>
            <Row label="Create empty folders" help="Create the movie or series folder when it is added, before there is a file.">
              <Toggle checked={values.createEmptyFolders} onChange={(v) => set({ createEmptyFolders: v })} />
            </Row>
            <Row label="Delete empty folders" help="Remove movie, series and season folders that became empty during a disk scan, import or rename.">
              <Toggle checked={values.deleteEmptyFolders} onChange={(v) => set({ deleteEmptyFolders: v })} />
            </Row>
            {values.createEmptyFolders && values.deleteEmptyFolders && (
              <p className={mm.notice}>With both on, empty movie and series folders are kept (only empty season folders are removed).</p>
            )}
          </Section>

          {!values.isWindows && (
            <Section id="permissions" title="Permissions" advanced description="Linux and macOS: permissions for imported files and new folders.">
              <Row label="Set permissions" help="Apply the mode and group below after an import.">
                <Toggle checked={values.setPermissions} onChange={(v) => set({ setPermissions: v })} />
              </Row>
              {values.setPermissions && (
                <>
                  <Row
                    label="chmod folder"
                    htmlFor="mm-chmod"
                    error={err('chmodFolder')}
                    help="Octal mode for folders, e.g. 755 or 775. Files get the same without the execute bits (644, 664)."
                  >
                    <input id="mm-chmod" className={`${ui.input} ${ui.mono} ${mm.short}`} value={values.chmodFolder} onChange={(e) => set({ chmodFolder: e.target.value })} />
                  </Row>
                  <Row label="chown group" htmlFor="mm-chown" error={err('chownGroup')} help="Group name or id, e.g. media. Empty: unchanged. StreamZH must be in that group.">
                    <input id="mm-chown" className={`${ui.input} ${ui.mono}`} value={values.chownGroup} onChange={(e) => set({ chownGroup: e.target.value })} />
                  </Row>
                </>
              )}
            </Section>
          )}

          <Section
            id="download-handling"
            title="Completed and failed downloads"
            description="What happens when a download of a movie or episode finishes or fails."
            hiddenCount={2}
          >
            <Row
              label="Completed download handling"
              help="Import finished downloads automatically. Off: use Import… in Activity. Downloads from the Search page or Add NZB always need Import…."
            >
              <Toggle checked={values.completedDownloadHandling} onChange={(v) => set({ completedDownloadHandling: v })} />
            </Row>
            <Row label="Remove completed downloads" advanced help="Remove imported downloads from Activity → History (the grab history keeps them).">
              <Toggle checked={values.removeCompletedDownloads} onChange={(v) => set({ removeCompletedDownloads: v })} />
            </Row>
            <Row label="Redownload failed" help="Search again right away when a download fails; the failed release goes on the blocklist.">
              <Toggle checked={values.redownloadFailed} onChange={(v) => set({ redownloadFailed: v })} />
            </Row>
            <Row label="Remove failed downloads" advanced help="Remove failed downloads of movies and episodes from Activity and delete their incomplete files.">
              <Toggle checked={values.removeFailedDownloads} onChange={(v) => set({ removeFailedDownloads: v })} />
            </Row>
          </Section>

          {save}
        </>
      )}
    </SettingsPage>
  )
}
