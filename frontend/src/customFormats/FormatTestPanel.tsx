import { useMutation, useQuery } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { customFormatsApi, implementationLabel, type FormatMediaType, type FormatTestResult } from '../api/customFormats'
import { profileKeys, profilesApi } from '../api/profiles'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import { useAdvanced } from '../lib/advancedMode'
import styles from './CustomFormats.module.css'
import { ScorePill } from './FormatTags'

/** «Test a release name»: which formats match a name, and its score in a chosen profile. */
export default function FormatTestPanel() {
  const advanced = useAdvanced()
  const profiles = useQuery({ queryKey: profileKeys.profiles, queryFn: profilesApi.profiles })
  const [title, setTitle] = useState('')
  const [mediaType, setMediaType] = useState<FormatMediaType>('movie')
  const [profileId, setProfileId] = useState('')
  const [sizeGb, setSizeGb] = useState('')
  const [showAll, setShowAll] = useState(false)

  const test = useMutation({
    mutationFn: () =>
      customFormatsApi.test({
        title: title.trim(),
        mediaType,
        qualityProfileId: Number(profileId) || undefined,
        sizeGb: Number(sizeGb) || undefined,
      }),
  })

  const submit = (event: FormEvent) => {
    event.preventDefault()
    if (title.trim()) test.mutate()
  }

  return (
    <section className={`${ui.panel} ${styles.testPanel}`} aria-label="Test a release name">
      <form className={styles.testForm} onSubmit={submit}>
        <label className={styles.testTitle}>
          <Icon name="sparkle" size={15} />
          <input
            className={`${ui.input} ${ui.mono}`}
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            placeholder="Test a release name, e.g. Dune.Part.Two.2024.German.DL.1080p.BluRay.x264-ZeroTwo"
            aria-label="Release name to test"
            spellCheck={false}
          />
        </label>
        <div className={styles.testOptions}>
          <div className={ui.seg} role="group" aria-label="Release of a">
            <button type="button" aria-pressed={mediaType === 'movie'} onClick={() => setMediaType('movie')}>
              Movie
            </button>
            <button type="button" aria-pressed={mediaType === 'series'} onClick={() => setMediaType('series')}>
              Episode
            </button>
          </div>
          <select className={`${ui.select} ${styles.testProfile}`} value={profileId} onChange={(e) => setProfileId(e.target.value)} aria-label="Score with profile">
            <option value="">No profile (no score)</option>
            {(profiles.data ?? []).map((p) => (
              <option key={p.id} value={p.id}>
                Score with {p.name}
              </option>
            ))}
          </select>
          {advanced && (
            <span className={styles.testSize}>
              <input
                className={`${ui.input} ${ui.num}`}
                inputMode="decimal"
                value={sizeGb}
                onChange={(e) => setSizeGb(e.target.value)}
                placeholder="Size"
                aria-label="Size in GB"
              />
              <span className={styles.unit}>GB</span>
            </span>
          )}
          <button className={ui.primary} type="submit" disabled={!title.trim() || test.isPending}>
            <Icon name="zap" size={15} />
            {test.isPending ? 'Testing…' : 'Test'}
          </button>
        </div>
      </form>
      {test.isError && <p className={ui.errBox}>{test.error.message}</p>}
      {test.data && <TestResult result={test.data} scored={!!profileId} showAll={showAll} onShowAll={setShowAll} />}
    </section>
  )
}

function TestResult({ result, scored, showAll, onShowAll }: { result: FormatTestResult; scored: boolean; showAll: boolean; onShowAll: (v: boolean) => void }) {
  const matched = result.formats.filter((f) => f.matched)
  const missed = result.formats.length - matched.length
  const list = showAll ? [...matched, ...result.formats.filter((f) => !f.matched)] : matched
  return (
    <div className={styles.testResult}>
      <div className={styles.testSummary}>
        <strong>
          {matched.length === 0 ? 'No format matches' : `${matched.length} format${matched.length === 1 ? '' : 's'} match`}
        </strong>
        {scored && <ScorePill score={result.totalScore} label="Score" />}
        {missed > 0 && (
          <button type="button" className={`${ui.ghost} ${ui.sm} ${styles.testToggle}`} onClick={() => onShowAll(!showAll)}>
            {showAll ? 'Hide the others' : `Show the ${missed} that do not`}
          </button>
        )}
      </div>
      {list.length > 0 && (
        <ul className={styles.testList}>
          {list.map((f) => (
            <li key={f.id} className={f.matched ? undefined : styles.testMissed}>
              <span className={styles.testName}>
                <Icon name={f.matched ? 'check' : 'minus'} size={13} />
                {f.name}
              </span>
              <span className={styles.testSpecs}>
                {f.specifications.map((s, i) => (
                  <span key={i} className={`${styles.specChip} ${s.matched ? styles.specOk : ''}`} title={implementationLabel(s.implementation)}>
                    {s.name}
                  </span>
                ))}
              </span>
              {scored && f.score != null && <ScorePill score={f.matched ? f.score : 0} title={f.matched ? 'Score in the profile' : `Would add ${f.score}`} />}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
