import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router'
import {
  emptyReleaseProfile,
  formatScore,
  indexerOptionsApi,
  indexerOptionsKeys,
  isRegexTerm,
  relativeTime,
  type IndexerOptions,
  type MinimumAvailability,
  type ReleaseProfile,
} from '../api/indexerOptions'
import { indexerKeys, indexersApi } from '../api/indexers'
import { profileKeys, profilesApi } from '../api/profiles'
import ui from '../components/ui.module.css'
import styles from './IndexerOptions.module.css'
import ReleaseProfileDialog from './ReleaseProfileDialog'
import { NumberInput, Row, SaveBar, Section, Select, SettingsPage, Toggle } from './SettingsKit'
import { TermInput } from './TermInput'
import { useSettingsForm } from './useSettingsForm'

type NumberField = 'minimumAge' | 'retention' | 'maximumSize' | 'availabilityDelay' | 'rssSyncInterval' | 'missingSearchInterval'

/** The form keeps empty number inputs as null; they are saved as 0. */
type Draft = Omit<IndexerOptions, NumberField> & Record<NumberField, number | null>

const optionFields = ['minimumAge', 'retention', 'maximumSize', 'allowHardcodedSubs', 'whitelistedHardcodedSubs', 'minimumAvailability', 'availabilityDelay'] as const
const automationFields = ['rssSyncInterval', 'missingSearchInterval', 'searchOnAdd'] as const

const availabilityOptions: { value: MinimumAvailability; label: string }[] = [
  { value: 'announced', label: 'Announced' },
  { value: 'inCinemas', label: 'In cinemas' },
  { value: 'released', label: 'Released' },
]

/**
 * One section of the shared settings resource: saves only its own fields on top of the latest saved values,
 * so unsaved changes in the other section are neither sent nor lost.
 */
function useSection(fields: readonly (keyof IndexerOptions)[]) {
  const queryClient = useQueryClient()
  return useSettingsForm<Draft>({
    queryKey: indexerOptionsKeys.options,
    load: indexerOptionsApi.options,
    save: (draft) => {
      const latest = queryClient.getQueryData<IndexerOptions>(indexerOptionsKeys.options)
      const merged = { ...(latest ?? draft) } as Record<string, unknown>
      for (const field of fields) merged[field] = draft[field] ?? 0
      return indexerOptionsApi.saveOptions(merged as unknown as IndexerOptions)
    },
    invalidate: [indexerOptionsKeys.tasks],
  })
}

/** Settings → Indexers: release rules for all indexers (Radarr's Indexer options), RSS and release profiles (Sonarr). */
export default function IndexerOptionsPage() {
  return (
    <SettingsPage title="Indexers" lead="Release rules that apply to all indexers. The indexers themselves are under Indexers in the sidebar.">
      <OptionsSection />
      <AutomationSection />
      <ReleaseProfilesSection />
    </SettingsPage>
  )
}

function OptionsSection() {
  const form = useSection(optionFields)
  const v = form.values
  if (!v) return form.query.error ? <p className={ui.error}>{form.query.error.message}</p> : null

  return (
    <Section
      id="options"
      title="Options"
      description="Checked for every release, in searches and RSS. Rejected releases show the reason in the manual search."
      hiddenCount={5}
      footer={<SaveBar dirty={form.dirty} saving={form.saving} saved={form.saved} error={form.formError} onSave={form.save} onReset={form.reset} />}
    >
      <Row label="Retention" htmlFor="io-retention" help="Your Usenet provider's retention. Older releases are rejected. 0 = unlimited." error={form.error('retention')}>
        <NumberInput id="io-retention" value={v.retention} onChange={(retention) => form.set({ retention })} unit="days" min={0} step={1} />
      </Row>
      <Row
        label="Maximum size"
        htmlFor="io-maximum-size"
        help="Releases larger than this are rejected, whatever the quality. 0 = unlimited."
        error={form.error('maximumSize')}
      >
        <NumberInput id="io-maximum-size" value={v.maximumSize} onChange={(maximumSize) => form.set({ maximumSize })} unit="MB" min={0} step={100} wide />
      </Row>
      <Row
        label="Minimum age"
        htmlFor="io-minimum-age"
        advanced
        help="New uploads need a moment to reach every Usenet server. Younger releases wait for the next RSS sync. 0 = off."
        error={form.error('minimumAge')}
      >
        <NumberInput id="io-minimum-age" value={v.minimumAge} onChange={(minimumAge) => form.set({ minimumAge })} unit="minutes" min={0} step={1} />
      </Row>
      <Row
        label="Allow hardcoded subs"
        advanced
        help="Off: releases with burned-in subtitles (HC, KORSUB, Subbed …) are rejected."
        error={form.error('allowHardcodedSubs')}
      >
        <Toggle checked={v.allowHardcodedSubs} onChange={(allowHardcodedSubs) => form.set({ allowHardcodedSubs })} />
      </Row>
      {!v.allowHardcodedSubs && (
        <Row
          label="Whitelisted subtitle tags"
          htmlFor="io-whitelist"
          advanced
          help="Tags that do not count as hardcoded subs, e.g. NLSUBS. Press Enter after each tag."
          error={form.error('whitelistedHardcodedSubs')}
        >
          <TermInput
            id="io-whitelist"
            label="Whitelisted subtitle tags"
            placeholder="Type a tag and press Enter"
            value={v.whitelistedHardcodedSubs}
            onChange={(whitelistedHardcodedSubs) => form.set({ whitelistedHardcodedSubs })}
          />
        </Row>
      )}
      <Row
        label="Minimum availability"
        htmlFor="io-availability"
        advanced
        help="From when RSS and the missing search may grab a movie. Released = cinema release + 90 days. The Search button always searches."
        error={form.error('minimumAvailability')}
      >
        <Select id="io-availability" value={v.minimumAvailability} onChange={(minimumAvailability) => form.set({ minimumAvailability })} options={availabilityOptions} />
      </Row>
      <Row
        label="Availability delay"
        htmlFor="io-delay"
        advanced
        help="Days added to that date; negative values start earlier."
        error={form.error('availabilityDelay')}
      >
        <NumberInput id="io-delay" value={v.availabilityDelay} onChange={(availabilityDelay) => form.set({ availabilityDelay })} unit="days" min={-365} max={365} step={1} />
      </Row>
    </Section>
  )
}

function AutomationSection() {
  const queryClient = useQueryClient()
  const form = useSection(automationFields)
  const tasks = useQuery({ queryKey: indexerOptionsKeys.tasks, queryFn: indexerOptionsApi.tasks, refetchInterval: 30_000 })
  const run = useMutation({
    mutationFn: () => indexerOptionsApi.runTask('rss-sync'),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: indexerOptionsKeys.tasks }),
  })
  const v = form.values
  if (!v) return null

  const rss = tasks.data?.find((t) => t.key === 'rss-sync')
  const rssState = rss
    ? rss.running
      ? 'Running now'
      : rss.queued
        ? 'Waiting to run'
        : [
            rss.lastRun ? `Last run ${relativeTime(rss.lastRun)}${rss.lastSuccess === false ? ' (failed)' : ''}` : 'Not run yet',
            rss.nextRun && rss.intervalMinutes ? `next ${relativeTime(rss.nextRun)}` : null,
          ]
            .filter(Boolean)
            .join(' · ')
    : null

  return (
    <Section
      id="rss"
      title="RSS & automation"
      description="RSS sync reads the newest releases of every indexer with RSS switched on and grabs what your monitored movies and episodes still need."
      hiddenCount={2}
      footer={<SaveBar dirty={form.dirty} saving={form.saving} saved={form.saved} error={form.formError} onSave={form.save} onReset={form.reset} />}
    >
      <Row
        label="RSS sync interval"
        htmlFor="io-rss"
        help={
          <>
            0 = off (nothing is grabbed on its own any more), otherwise 10 to 120 minutes.
            {rssState && (
              <>
                {' '}
                {rssState}
                {rss?.lastError ? `: ${rss.lastError}` : ''}.
              </>
            )}
          </>
        }
        error={form.error('rssSyncInterval')}
      >
        <div className={styles.picks}>
          <NumberInput id="io-rss" value={v.rssSyncInterval} onChange={(rssSyncInterval) => form.set({ rssSyncInterval })} unit="minutes" min={0} max={120} step={5} />
          <button className={ui.button} type="button" onClick={() => run.mutate()} disabled={run.isPending || !!rss?.running || !!rss?.queued}>
            Sync now
          </button>
        </div>
      </Row>
      <Row
        label="Search for missing"
        htmlFor="io-missing"
        advanced
        help="Searches all missing movies and episodes regularly, on top of RSS. Costs many API hits. 0 = off, otherwise 6 to 720 hours."
        error={form.error('missingSearchInterval')}
      >
        <NumberInput
          id="io-missing"
          value={v.missingSearchInterval}
          onChange={(missingSearchInterval) => form.set({ missingSearchInterval })}
          unit="hours"
          min={0}
          max={720}
          step={1}
        />
      </Row>
      <Row label="Search when adding" advanced help='Default for "Start search for missing" when you add a movie or series.' error={form.error('searchOnAdd')}>
        <Toggle checked={v.searchOnAdd} onChange={(searchOnAdd) => form.set({ searchOnAdd })} />
      </Row>
    </Section>
  )
}

function ReleaseProfilesSection() {
  const profiles = useQuery({ queryKey: indexerOptionsKeys.releaseProfiles, queryFn: indexerOptionsApi.releaseProfiles })
  const indexers = useQuery({ queryKey: indexerKeys.list, queryFn: indexersApi.list })
  const qualityProfiles = useQuery({ queryKey: profileKeys.profiles, queryFn: profilesApi.profiles })
  const [editing, setEditing] = useState<ReleaseProfile | null>(null)

  const indexerName = (id: number) => indexers.data?.find((i) => i.id === id)?.name ?? `#${id}`
  const profileName = (id: number) => qualityProfiles.data?.find((p) => p.id === id)?.name ?? `#${id}`

  return (
    <Section
      id="release-profiles"
      title="Release profiles"
      description={
        <>
          Terms a release must contain, must not contain, or that make it preferred (like Sonarr). Try them on the{' '}
          <Link to="/settings/release-test">release test</Link> page.
        </>
      }
    >
      {profiles.error && <p className={ui.error}>{profiles.error.message}</p>}
      <div className={styles.profiles}>
        {(profiles.data ?? []).map((p) => (
          <button key={p.id} type="button" className={`${styles.profile} ${p.enabled ? '' : styles.profileOff}`} onClick={() => setEditing(p)}>
            <span className={styles.profileHead}>
              <span>{p.name}</span>
              {!p.enabled && <span className={`${ui.badge} ${ui.idle}`}>Off</span>}
            </span>
            <TermLine label="Must contain" terms={p.required} />
            <TermLine label="Must not contain" terms={p.ignored} />
            <TermLine label="Preferred" terms={p.preferred.map((t) => `${t.term} ${formatScore(t.score)}`)} raw={p.preferred.map((t) => t.term)} />
            {p.indexerIds.length > 0 && <TermLine label="Indexers" terms={p.indexerIds.map(indexerName)} plain />}
            {p.qualityProfileIds.length > 0 && <TermLine label="Quality profiles" terms={p.qualityProfileIds.map(profileName)} plain />}
          </button>
        ))}
        <button type="button" className={styles.addProfile} onClick={() => setEditing(emptyReleaseProfile)}>
          + Add release profile
        </button>
      </div>
      {editing && <ReleaseProfileDialog profile={editing} onClose={() => setEditing(null)} />}
    </Section>
  )
}

function TermLine({ label, terms, raw, plain = false }: { label: string; terms: string[]; raw?: string[]; plain?: boolean }) {
  if (terms.length === 0) return null
  return (
    <span className={styles.profileLine}>
      <strong>{label}</strong>
      {terms.map((t, i) => (
        <span key={t} className={`${styles.mini} ${!plain && isRegexTerm(raw?.[i] ?? t) ? styles.miniRegex : ''}`}>
          {t}
        </span>
      ))}
    </span>
  )
}
