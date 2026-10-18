import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { customFormatsApi, implementationLabel } from '../api/customFormats'
import type { DecisionResult, ParsedRelease } from '../api/parse'
import { profileKeys, profilesApi } from '../api/profiles'
import Icon from '../components/Icon'
import { Skeleton } from '../components/Loader'
import ui from '../components/ui.module.css'
import styles from './CustomFormats.module.css'
import { FormatTags, ScorePill } from './FormatTags'

/**
 * Release test → «Custom formats»: which formats the name matches (with the conditions that did) and, when the name
 * is checked against a library item, the score in that item's profile.
 */
export default function ParseFormatsPanel({
  title,
  parsed,
  decision,
  sizeMb,
}: {
  title: string
  parsed: ParsedRelease
  decision: DecisionResult | null
  sizeMb: number | null
}) {
  const mediaType = parsed.kind === 'Episode' ? 'series' : 'movie'
  const profiles = useQuery({ queryKey: profileKeys.profiles, queryFn: profilesApi.profiles, staleTime: 60_000 })
  const profileId = decision ? profiles.data?.find((p) => p.name === decision.profile)?.id : undefined
  const test = useQuery({
    queryKey: ['custom-format-test', title, mediaType, sizeMb, profileId ?? null],
    queryFn: () => customFormatsApi.test({ title, mediaType, sizeGb: sizeMb ? sizeMb / 1024 : undefined, qualityProfileId: profileId }),
    enabled: !decision || profiles.isFetched,
    retry: false,
  })

  const matched = test.data?.formats.filter((f) => f.matched) ?? []
  const score = decision?.customFormatScore ?? (profileId ? test.data?.totalScore : undefined)

  return (
    <section className={ui.panel}>
      <div className={styles.parseHead}>
        <Icon name="tag" size={17} className={styles.parseIcon} />
        <h2>Custom formats</h2>
        {score !== undefined && <ScorePill score={score} label={decision ? decision.profile : 'Score'} title="Custom format score in the profile" />}
      </div>
      {test.isPending && !decision && <Skeleton rows={2} />}
      {test.data ? (
        matched.length === 0 ? (
          <p className={styles.parseNote}>
            No {mediaType === 'movie' ? 'movie' : 'series'} format matches this name. Formats are set up under{' '}
            <Link to="/settings/custom-formats">Settings → Custom formats</Link>.
          </p>
        ) : (
          <ul className={`${styles.testList} ${styles.stacked}`}>
            {matched.map((f) => (
              <li key={f.id}>
                <span className={styles.testName}>
                  <Icon name="check" size={13} />
                  {f.name}
                </span>
                <span className={styles.testSpecs}>
                  {f.specifications
                    .filter((s) => s.matched)
                    .map((s, i) => (
                      <span key={i} className={`${styles.specChip} ${styles.specOk}`} title={implementationLabel(s.implementation)}>
                        {s.name}
                      </span>
                    ))}
                </span>
                {profileId !== undefined && f.score != null && <ScorePill score={f.score} />}
              </li>
            ))}
          </ul>
        )
      ) : decision?.customFormats ? (
        // Older server without the test endpoint: what the decision says.
        decision.customFormats.length > 0 ? (
          <FormatTags formats={decision.customFormats} max={20} />
        ) : (
          <p className={styles.parseNote}>No format matches this name.</p>
        )
      ) : test.isError ? (
        <p className={styles.parseNote}>{test.error.message}</p>
      ) : null}
      {!decision && test.data && matched.length > 0 && (
        <p className={styles.parseNote}>Choose a movie or series under “Check against” to see the score in its profile.</p>
      )}
    </section>
  )
}
