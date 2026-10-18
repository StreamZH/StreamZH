import { importListApi, importListKeys, type ImportListOptions } from '../api/importLists'
import { useAuth } from '../auth/auth'
import { NumberInput, Row, SaveBar, Section, Select } from '../settings/SettingsKit'
import { useSettingsForm } from '../settings/useSettingsForm'
import { cleanLevelHelp, cleanLevelOptions } from './labels'

/** Options for all lists (Radarr's "Import list options"): clean library level and, for experts, the update interval. */
export function OptionsSection() {
  const { user } = useAuth()
  const canEdit = user?.role === 'Owner' || user?.role === 'Admin'
  const f = useSettingsForm<ImportListOptions>({
    queryKey: importListKeys.options,
    load: importListApi.options,
    save: importListApi.saveOptions,
  })
  const v = f.values

  return (
    <Section
      id="options"
      title="Options"
      description="For all lists."
      hiddenCount={1}
      footer={<SaveBar dirty={f.dirty} saving={f.saving} saved={f.saved} error={f.formError} onSave={f.save} onReset={f.reset} disabled={!canEdit} />}
    >
      {!v ? (
        <p>{f.query.isError ? f.query.error.message : 'Loading…'}</p>
      ) : (
        <>
          <Row
            label="Clean library"
            htmlFor="il-clean"
            error={f.error('cleanLibraryLevel')}
            help={
              <>
                For items a list added that are on no enabled list any more. {cleanLevelHelp[v.cleanLibraryLevel]} Items you added yourself are
                never touched, and nothing happens while a list fails.
              </>
            }
          >
            <Select id="il-clean" value={v.cleanLibraryLevel} onChange={(cleanLibraryLevel) => f.set({ cleanLibraryLevel })} options={cleanLevelOptions} />
          </Row>
          <Row
            label="List update interval"
            htmlFor="il-interval"
            advanced
            error={f.error('syncInterval')}
            help="How often all lists are read. Lists can also be synced by hand. 6 to 720 hours."
          >
            <NumberInput id="il-interval" value={v.syncInterval} onChange={(syncInterval) => f.set({ syncInterval: syncInterval ?? 0 })} unit="hours" min={6} max={720} />
          </Row>
        </>
      )}
    </Section>
  )
}
