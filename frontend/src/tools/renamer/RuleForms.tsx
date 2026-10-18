import { useId, type ReactNode } from 'react'
import type { CaseRule, CounterRule, ExtensionRule, InsertRule, MediaRule, RemoveRule, ReplaceRule, RuleScope, TrimRule } from '../../api/renamer'
import ui from '../../components/ui.module.css'
import { useAdvanced } from '../../lib/advancedMode'
import FormatField from '../../mediaManagement/FormatField'
import RegexTester from './RegexTester'
import styles from './Renamer.module.css'
import { episodeFormats, movieFormats } from './rules'

/** Label above the control, for the compact rule cards. */
export function Field({ label, children, wide = false, hint }: { label: string; children: ReactNode; wide?: boolean; hint?: ReactNode }) {
  return (
    <label className={`${styles.field} ${wide ? styles.fieldWide : ''}`}>
      <span className={styles.fieldLabel}>{label}</span>
      {children}
      {hint && <small className={styles.fieldHint}>{hint}</small>}
    </label>
  )
}

export function Check({ checked, onChange, children }: { checked: boolean; onChange: (checked: boolean) => void; children: ReactNode }) {
  return (
    <label className={`${ui.check} ${styles.checkInline}`}>
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      {children}
    </label>
  )
}

function Num({ value, onChange, min, max, placeholder }: { value: number | null; onChange: (v: number | null) => void; min?: number; max?: number; placeholder?: string }) {
  return (
    <input
      className={`${ui.input} ${styles.num}`}
      type="number"
      inputMode="numeric"
      min={min}
      max={max}
      placeholder={placeholder}
      value={value ?? ''}
      onChange={(e) => onChange(e.target.value === '' ? null : Math.trunc(e.target.valueAsNumber))}
    />
  )
}

function Scope({ value, onChange }: { value: RuleScope; onChange: (v: RuleScope) => void }) {
  return (
    <Field label="Applies to">
      <select className={ui.select} value={value} onChange={(e) => onChange(e.target.value as RuleScope)}>
        <option value="name">Name without extension</option>
        <option value="fullName">Whole name with extension</option>
      </select>
    </Field>
  )
}

type Props<R> = { rule: R; set: (patch: Partial<R>) => void; sample: string | null }

export function ReplaceForm({ rule, set, sample }: Props<ReplaceRule>) {
  const advanced = useAdvanced()
  return (
    <>
      <div className={styles.fields}>
        <Field label={rule.useRegex ? 'Find (regular expression)' : 'Find'} wide>
          <input className={`${ui.input} ${ui.mono}`} value={rule.find} spellCheck={false} onChange={(e) => set({ find: e.target.value })} placeholder={rule.useRegex ? '^(.+?)\\.S(\\d+)E(\\d+)' : 'Text to find'} />
        </Field>
        <Field label="Replace with" wide hint={rule.useRegex ? <>Use $1, $2 or {'${name}'} for groups; empty removes the match.</> : 'Empty removes the text.'}>
          <input className={`${ui.input} ${ui.mono}`} value={rule.replaceWith} spellCheck={false} onChange={(e) => set({ replaceWith: e.target.value })} placeholder={rule.useRegex ? '$1 - S$2E$3' : ''} />
        </Field>
      </div>
      <div className={styles.checks}>
        <Check checked={rule.useRegex} onChange={(useRegex) => set({ useRegex })}>
          Regular expression
        </Check>
        <Check checked={rule.caseSensitive} onChange={(caseSensitive) => set({ caseSensitive })}>
          Match case
        </Check>
      </div>
      {advanced && (
        <div className={styles.fields}>
          <Field label="Occurrences">
            <select className={ui.select} value={rule.occurrence} onChange={(e) => set({ occurrence: e.target.value as ReplaceRule['occurrence'] })}>
              <option value="all">All</option>
              <option value="first">First only</option>
              <option value="last">Last only</option>
            </select>
          </Field>
          <Scope value={rule.scope} onChange={(scope) => set({ scope })} />
        </div>
      )}
      {rule.useRegex && rule.find && <RegexTester pattern={rule.find} caseSensitive={rule.caseSensitive} replacement={rule.replaceWith} sample={sample} />}
    </>
  )
}

export function RemoveForm({ rule, set }: Props<RemoveRule>) {
  const advanced = useAdvanced()
  return (
    <>
      <div className={styles.fields}>
        <Field label="Remove">
          <select className={ui.select} value={rule.mode} onChange={(e) => set({ mode: e.target.value as RemoveRule['mode'] })}>
            <option value="brackets">Brackets with content</option>
            <option value="separators">Dots and underscores (to spaces)</option>
            <option value="spaces">Double spaces</option>
            <option value="between">Text between markers</option>
            <option value="characters">Characters at a position</option>
          </select>
        </Field>
        {rule.mode === 'characters' && (
          <>
            <Field label="Count">
              <Num value={rule.count} min={1} max={1000} onChange={(v) => set({ count: v ?? 1 })} />
            </Field>
            <Field label={rule.fromEnd ? 'From the end, position' : 'From position'}>
              <Num value={rule.position} min={1} max={1000} onChange={(v) => set({ position: v ?? 1 })} />
            </Field>
          </>
        )}
        {rule.mode === 'between' && (
          <>
            <Field label="Start">
              <input className={`${ui.input} ${ui.mono} ${styles.short}`} value={rule.startMarker} onChange={(e) => set({ startMarker: e.target.value })} />
            </Field>
            <Field label="End">
              <input className={`${ui.input} ${ui.mono} ${styles.short}`} value={rule.endMarker} onChange={(e) => set({ endMarker: e.target.value })} />
            </Field>
          </>
        )}
      </div>
      <div className={styles.checks}>
        {rule.mode === 'brackets' && (
          <>
            <Check checked={rule.square} onChange={(square) => set({ square })}>
              [ … ]
            </Check>
            <Check checked={rule.round} onChange={(round) => set({ round })}>
              ( … )
            </Check>
            <Check checked={rule.curly} onChange={(curly) => set({ curly })}>
              {'{ … }'}
            </Check>
          </>
        )}
        {rule.mode === 'characters' && (
          <Check checked={rule.fromEnd} onChange={(fromEnd) => set({ fromEnd })}>
            Count from the end
          </Check>
        )}
        {rule.mode === 'between' && (
          <Check checked={rule.includeMarkers} onChange={(includeMarkers) => set({ includeMarkers })}>
            Remove the markers too
          </Check>
        )}
        {rule.mode === 'separators' && (
          <Check checked={rule.keepDecimalDots} onChange={(keepDecimalDots) => set({ keepDecimalDots })}>
            Keep dots between digits (5.1)
          </Check>
        )}
      </div>
      {advanced && (
        <div className={styles.fields}>
          <Scope value={rule.scope} onChange={(scope) => set({ scope })} />
        </div>
      )}
    </>
  )
}

export function InsertForm({ rule, set }: Props<InsertRule>) {
  const advanced = useAdvanced()
  return (
    <>
      <div className={styles.fields}>
        <Field label="Text" wide hint={<>Tokens: {'{name} {ext} {folder} {date} {time} {year} {index}'}</>}>
          <input className={`${ui.input} ${ui.mono}`} value={rule.text} spellCheck={false} onChange={(e) => set({ text: e.target.value })} placeholder="{folder} - " />
        </Field>
        <Field label="Where">
          <select className={ui.select} value={rule.where} onChange={(e) => set({ where: e.target.value as InsertRule['where'] })}>
            <option value="prefix">At the start</option>
            <option value="suffix">At the end</option>
            <option value="position">At a position</option>
          </select>
        </Field>
        {rule.where === 'position' && (
          <Field label="Before character">
            <Num value={rule.position} min={1} max={1000} onChange={(v) => set({ position: v ?? 1 })} />
          </Field>
        )}
      </div>
      {rule.where === 'position' && (
        <div className={styles.checks}>
          <Check checked={rule.fromEnd} onChange={(fromEnd) => set({ fromEnd })}>
            Count from the end
          </Check>
        </div>
      )}
      {advanced && (
        <div className={styles.fields}>
          <Scope value={rule.scope} onChange={(scope) => set({ scope })} />
        </div>
      )}
    </>
  )
}

const languages = [
  { value: 'en', label: 'English' },
  { value: 'de', label: 'German' },
  { value: 'fr', label: 'French' },
  { value: 'es', label: 'Spanish' },
  { value: 'it', label: 'Italian' },
  { value: 'nl', label: 'Dutch' },
  { value: 'pt', label: 'Portuguese' },
  { value: 'none', label: 'None (every word)' },
]

export function CaseForm({ rule, set }: Props<CaseRule>) {
  const advanced = useAdvanced()
  return (
    <>
      <div className={styles.fields}>
        <Field label="Case">
          <select className={ui.select} value={rule.mode} onChange={(e) => set({ mode: e.target.value as CaseRule['mode'] })}>
            <option value="title">Title Case</option>
            <option value="sentence">Sentence case</option>
            <option value="lower">lower case</option>
            <option value="upper">UPPER CASE</option>
          </select>
        </Field>
        {rule.mode === 'title' && (
          <Field label="Small words" hint="Words like “of” and “the” stay lower case.">
            <select className={ui.select} value={rule.language} onChange={(e) => set({ language: e.target.value })}>
              {languages.map((l) => (
                <option key={l.value} value={l.value}>
                  {l.label}
                </option>
              ))}
            </select>
          </Field>
        )}
      </div>
      {(rule.mode === 'title' || rule.mode === 'sentence') && (
        <div className={styles.checks}>
          <Check checked={rule.keepCapitals} onChange={(keepCapitals) => set({ keepCapitals })}>
            Keep words in capitals (FBI, NCIS)
          </Check>
        </div>
      )}
      {advanced && (
        <div className={styles.fields}>
          <Scope value={rule.scope} onChange={(scope) => set({ scope })} />
        </div>
      )}
    </>
  )
}

export function CounterForm({ rule, set }: Props<CounterRule>) {
  return (
    <>
      <div className={styles.fields}>
        <Field label="Start">
          <Num value={rule.start} onChange={(v) => set({ start: v ?? 1 })} />
        </Field>
        <Field label="Step">
          <Num value={rule.step} onChange={(v) => set({ step: v ?? 1 })} />
        </Field>
        <Field label="Digits">
          <Num value={rule.padding} min={0} max={10} onChange={(v) => set({ padding: v ?? 0 })} />
        </Field>
        <Field label="Where">
          <select className={ui.select} value={rule.where} onChange={(e) => set({ where: e.target.value as CounterRule['where'] })}>
            <option value="prefix">At the start</option>
            <option value="suffix">At the end</option>
            <option value="position">At a position</option>
            <option value="replace">Instead of the name</option>
          </select>
        </Field>
        {rule.where === 'position' && (
          <Field label="Before character">
            <Num value={rule.position} min={1} max={1000} onChange={(v) => set({ position: v ?? 1 })} />
          </Field>
        )}
        {(rule.where === 'prefix' || rule.where === 'suffix') && (
          <Field label="Separator">
            <input className={`${ui.input} ${ui.mono} ${styles.short}`} value={rule.separator} onChange={(e) => set({ separator: e.target.value })} />
          </Field>
        )}
      </div>
      <div className={styles.checks}>
        <Check checked={rule.perFolder} onChange={(perFolder) => set({ perFolder })}>
          Start again in every folder
        </Check>
      </div>
    </>
  )
}

export function ExtensionForm({ rule, set }: Props<ExtensionRule>) {
  return (
    <div className={styles.fields}>
      <Field label="Extension">
        <select className={ui.select} value={rule.mode} onChange={(e) => set({ mode: e.target.value as ExtensionRule['mode'] })}>
          <option value="lower">lower case (.mkv)</option>
          <option value="upper">UPPER CASE (.MKV)</option>
          <option value="change">Change to …</option>
          <option value="remove">Remove</option>
        </select>
      </Field>
      {rule.mode === 'change' && (
        <Field label="New extension">
          <input className={`${ui.input} ${ui.mono} ${styles.short}`} value={rule.newExtension} placeholder="mkv" onChange={(e) => set({ newExtension: e.target.value })} />
        </Field>
      )}
    </div>
  )
}

export function TrimForm({ rule, set }: Props<TrimRule>) {
  const advanced = useAdvanced()
  return (
    <div className={styles.fields}>
      <Field label="Characters" hint="Removed at the ends of the name.">
        <input className={`${ui.input} ${ui.mono} ${styles.short}`} value={rule.characters} onChange={(e) => set({ characters: e.target.value })} />
      </Field>
      <Field label="Where">
        <select className={ui.select} value={rule.where} onChange={(e) => set({ where: e.target.value as TrimRule['where'] })}>
          <option value="both">Both ends</option>
          <option value="start">Start</option>
          <option value="end">End</option>
        </select>
      </Field>
      <Field label="Maximum length">
        <Num value={rule.maxLength} min={1} max={255} placeholder="No limit" onChange={(maxLength) => set({ maxLength })} />
      </Field>
      {advanced && <Scope value={rule.scope} onChange={(scope) => set({ scope })} />}
    </div>
  )
}

export function MediaForm({ rule, set, settingsFormats }: Props<MediaRule> & { settingsFormats: { episode: string; movie: string } | null }) {
  const advanced = useAdvanced()
  const showEpisode = rule.kind !== 'movie'
  const showMovie = rule.kind !== 'episode'
  return (
    <>
      <div className={styles.fields}>
        <Field label="Files are">
          <select className={ui.select} value={rule.kind} onChange={(e) => set({ kind: e.target.value as MediaRule['kind'] })}>
            <option value="auto">Detect (episodes or movies)</option>
            <option value="episode">Episodes</option>
            <option value="movie">Movies</option>
          </select>
        </Field>
        <Field label="Look up in">
          <select className={ui.select} value={rule.match} onChange={(e) => set({ match: e.target.value as MediaRule['match'] })}>
            <option value="libraryAndTmdb">Library, then TMDB</option>
            <option value="library">Library only</option>
            <option value="tmdb">TMDB only</option>
            <option value="none">Nothing (name only)</option>
          </select>
        </Field>
        {showEpisode && (
          <Field label="Season" hint="For names with only an episode number, or a wrong season.">
            <Num value={rule.season} min={0} max={1000} placeholder="From the name" onChange={(season) => set({ season })} />
          </Field>
        )}
        {showEpisode && advanced && (
          <Field label="Episode offset" hint="Added to every episode number, e.g. -12.">
            <Num value={rule.episodeOffset} min={-1000} max={1000} onChange={(v) => set({ episodeOffset: v ?? 0 })} />
          </Field>
        )}
      </div>

      {showEpisode && (
        <FormatPicker
          label="Episode name"
          kind="episodeFile"
          value={rule.episodeFormat}
          options={episodeFormats}
          fallback={settingsFormats?.episode ?? null}
          onChange={(episodeFormat) => set({ episodeFormat })}
        />
      )}
      {showMovie && (
        <FormatPicker
          label="Movie name"
          kind="movieFile"
          value={rule.movieFormat}
          options={movieFormats}
          fallback={settingsFormats?.movie ?? null}
          onChange={(movieFormat) => set({ movieFormat })}
        />
      )}

      <div className={styles.checks}>
        <Check checked={rule.useFolderName} onChange={(useFolderName) => set({ useFolderName })}>
          Title from the folder when the name has none
        </Check>
      </div>
      {advanced && (
        <div className={styles.fields}>
          <Field label="Look up as" hint="A title to search instead of the one in the names.">
            <input className={ui.input} value={rule.searchTitle} onChange={(e) => set({ searchTitle: e.target.value })} placeholder="From the name" />
          </Field>
          <Field label="TMDB id" hint="When the search picks the wrong one.">
            <Num value={rule.tmdbId} min={1} placeholder="Search" onChange={(tmdbId) => set({ tmdbId })} />
          </Field>
        </div>
      )}
    </>
  )
}

function FormatPicker({
  label,
  kind,
  value,
  options,
  fallback,
  onChange,
}: {
  label: string
  kind: 'episodeFile' | 'movieFile'
  value: string
  options: { label: string; value: string }[]
  fallback: string | null
  onChange: (value: string) => void
}) {
  const id = useId()
  const known = options.some((o) => o.value === value)
  return (
    <div className={styles.format}>
      <Field label={label}>
        <select className={ui.select} value={known ? value : '__custom'} onChange={(e) => e.target.value !== '__custom' && onChange(e.target.value)}>
          {options.map((o) => (
            <option key={o.label} value={o.value}>
              {o.label}
            </option>
          ))}
          {!known && <option value="__custom">Custom</option>}
        </select>
      </Field>
      <FormatField
        id={id}
        value={value}
        onChange={onChange}
        kind={kind}
        example={value === '' && fallback ? fallback : null}
        exampleLabel="From the settings"
      />
    </div>
  )
}
