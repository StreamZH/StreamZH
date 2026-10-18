import { useQuery } from '@tanstack/react-query'
import { useState, type FormEvent, type ReactNode } from 'react'
import { Link } from 'react-router'
import { formatScore } from '../api/indexerOptions'
import { indexerKeys, indexersApi } from '../api/indexers'
import { libraryApi, queryKeys } from '../api/library'
import { parseRelease, type ParsedRelease, type ParseResult } from '../api/parse'
import Icon from '../components/Icon'
import Loader from '../components/Loader'
import ui from '../components/ui.module.css'
import { ScorePill } from '../customFormats/FormatTags'
import ParseFormatsPanel from '../customFormats/ParseFormatsPanel'
import { SettingsPage } from '../settings/SettingsKit'
import styles from './ParseTest.module.css'

const examples = [
  'Dune.Part.Two.2024.German.DL.2160p.WEB.H265-GROUP',
  'Interstellar.2014.German.BDRip.DL.1080p.x265.AAC-2BA',
  'Jackass.2026.GERMAN.CAM.1080p.x264-LiNEUP',
  'Breaking.Bad.S01E02.German.DL.720p.WEB.h264-GRP',
]

/** Tools → Release test: how StreamZH reads a release name and whether it would grab it (CloudZH key/value panels). */
export default function ParseTest() {
  const [title, setTitle] = useState('')
  const [target, setTarget] = useState('')
  const [sizeMb, setSizeMb] = useState('')
  const [indexerId, setIndexerId] = useState('')
  const [submitted, setSubmitted] = useState<{ title: string; target: string; size: number | null; indexerId: number | null } | null>(null)

  const movies = useQuery({ queryKey: queryKeys.movies, queryFn: libraryApi.movies })
  const series = useQuery({ queryKey: queryKeys.series, queryFn: libraryApi.series })
  const indexers = useQuery({ queryKey: indexerKeys.list, queryFn: indexersApi.list })

  const result = useQuery({
    queryKey: ['parse', submitted],
    queryFn: () => parseRelease(submitted!.title, submitted!.target, submitted!.size, submitted!.indexerId),
    enabled: !!submitted?.title,
  })

  const submit = (event: FormEvent) => {
    event.preventDefault()
    if (title.trim()) setSubmitted({ title: title.trim(), target, size: Number(sizeMb) || null, indexerId: Number(indexerId) || null })
  }

  const decision = result.data?.decision

  return (
    <SettingsPage eyebrow="Tools" advancedToggle={false} title="Release test" lead="Paste a release name to see how StreamZH reads it and whether it would grab it.">
      <form className={ui.panel} onSubmit={submit}>
        <div className={styles.head}>
          <Icon name="sparkle" size={17} className={styles.headIcon} />
          <h2>Release</h2>
        </div>
        <label className={styles.titleLabel} htmlFor="parse-title">
          Release name
        </label>
        <div className={styles.titleRow}>
          <input
            id="parse-title"
            className={`${ui.input} ${ui.mono}`}
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            placeholder="Some.Movie.2024.German.DL.1080p.WEB.h264-GROUP"
            spellCheck={false}
            autoFocus
          />
          <button className={ui.primary} type="submit" disabled={!title.trim()}>
            <Icon name="zap" size={15} />
            Test
          </button>
        </div>
        <div className={styles.examples}>
          <span className={styles.examplesLabel}>Examples</span>
          {examples.map((e) => (
            <button key={e} type="button" className={styles.example} onClick={() => setTitle(e)} aria-pressed={title === e}>
              {e}
            </button>
          ))}
        </div>
        <div className={styles.row}>
          <label className={ui.field}>
            <span>Check against (optional)</span>
            <select className={ui.select} value={target} onChange={(e) => setTarget(e.target.value)}>
              <option value="">Only parse</option>
              {(movies.data ?? []).length > 0 && (
                <optgroup label="Movies">
                  {movies.data!.map((m) => (
                    <option key={m.id} value={`movie:${m.id}`}>
                      {m.title} ({m.year})
                    </option>
                  ))}
                </optgroup>
              )}
              {(series.data ?? []).length > 0 && (
                <optgroup label="Series">
                  {series.data!.map((s) => (
                    <option key={s.id} value={`series:${s.id}`}>
                      {s.title} ({s.year})
                    </option>
                  ))}
                </optgroup>
              )}
            </select>
          </label>
          <label className={ui.field}>
            <span>Indexer (optional)</span>
            <select className={ui.select} value={indexerId} onChange={(e) => setIndexerId(e.target.value)}>
              <option value="">Any</option>
              {(indexers.data ?? []).map((i) => (
                <option key={i.id} value={i.id}>
                  {i.name}
                </option>
              ))}
            </select>
          </label>
          <label className={ui.field}>
            <span>Size in MB (optional)</span>
            <input className={`${ui.input} ${ui.num}`} inputMode="decimal" value={sizeMb} onChange={(e) => setSizeMb(e.target.value)} placeholder="e.g. 8500" />
          </label>
        </div>
      </form>

      {result.isFetching && !result.data && (
        <div className={ui.panel}>
          <Loader text="Reading the name…" />
        </div>
      )}
      {result.isError && <p className={ui.errBox}>{result.error.message}</p>}
      {result.data && !result.data.parsed && (
        <div className={`${ui.panel} ${styles.unreadable}`}>
          <span className={`${styles.verdictIcon} ${styles.verdictBad}`} aria-hidden="true">
            <Icon name="alert" size={18} />
          </span>
          <span>StreamZH could not read this name.</span>
        </div>
      )}

      {decision && (
        <section className={`${ui.panel} ${styles.verdict} ${decision.accepted ? styles.accepted : styles.rejected}`} aria-live="polite">
          <div className={styles.verdictHead}>
            <span className={`${styles.verdictIcon} ${decision.accepted ? styles.verdictOk : styles.verdictBad}`} aria-hidden="true">
              <Icon name={decision.accepted ? 'check' : 'x'} size={18} />
            </span>
            <div className={styles.verdictText}>
              <h2>
                {decision.accepted ? 'Would be grabbed' : 'Would be rejected'} for {decision.target}
              </h2>
              <div className={styles.pills}>
                <span className={ui.pill}>
                  <Icon name="star" size={12} />
                  {decision.profile}
                </span>
                {decision.customFormatScore !== undefined && (
                  <ScorePill score={decision.customFormatScore} label="Format score" title="Custom format score in this profile" />
                )}
                {result.data!.releaseProfiles.length > 0 && (
                  <span className={`${ui.pill} ${decision.preferredScore > 0 ? ui.pillOk : decision.preferredScore < 0 ? ui.pillErr : ''}`}>
                    Preferred score {formatScore(decision.preferredScore)}
                  </span>
                )}
              </div>
            </div>
          </div>
          {decision.rejections.length > 0 && (
            <ul className={styles.reasons}>
              {decision.rejections.map((r) => (
                <li key={r.code}>
                  <Icon name="ban" size={14} />
                  {r.message}
                </li>
              ))}
            </ul>
          )}
        </section>
      )}

      {result.data?.parsed && (
        <div className={styles.results}>
          <ParsedTable parsed={result.data.parsed} hardcodedSubs={result.data.hardcodedSubs} />
          <div className={styles.side}>
            <ParseFormatsPanel title={submitted!.title} parsed={result.data.parsed} decision={decision ?? null} sizeMb={submitted!.size} />
            <ReleaseProfiles result={result.data} />
          </div>
        </div>
      )}
    </SettingsPage>
  )
}

/** Which release profiles match the name, with the terms found and the preferred score. */
function ReleaseProfiles({ result }: { result: ParseResult }) {
  const matches = result.releaseProfiles
  return (
    <section className={ui.panel}>
      <div className={styles.head}>
        <Icon name="filter" size={17} className={styles.headIcon} />
        <h2>Release profiles</h2>
        {matches.length > 0 && (
          <span className={`${ui.pill} ${result.preferredScore > 0 ? ui.pillOk : result.preferredScore < 0 ? ui.pillErr : ''} ${styles.headAside}`}>
            Score {formatScore(result.preferredScore)}
          </span>
        )}
      </div>
      {matches.length === 0 ? (
        <p className={styles.note}>
          No release profile matches this name. Release profiles are set up under <Link to="/settings/indexers#release-profiles">Settings → Indexers</Link>.
        </p>
      ) : (
        <ul className={styles.profiles}>
          {matches.map((m) => (
            <li key={m.id} className={m.rejects ? styles.profileRejects : undefined}>
              <div className={styles.profileHead}>
                <strong>{m.name}</strong>
                {m.rejects && <span className={`${ui.pill} ${ui.pillErr}`}>Rejects</span>}
              </div>
              {m.limited && <span className={styles.note}>Only some indexers or quality profiles</span>}
              <div className={styles.terms}>
                {m.missingRequired && <Term tone="bad">Contains none of {m.required.map((t) => `'${t}'`).join(', ')}</Term>}
                {m.requiredMatched.map((t) => (
                  <Term key={`r-${t}`} tone="ok">
                    Contains '{t}'
                  </Term>
                ))}
                {m.ignoredMatched.map((t) => (
                  <Term key={`i-${t}`} tone="bad">
                    Contains '{t}' (must not)
                  </Term>
                ))}
                {m.preferredMatched.map((p) => (
                  <Term key={`p-${p.term}`} tone={p.score > 0 ? 'ok' : 'bad'}>
                    '{p.term}' {formatScore(p.score)}
                  </Term>
                ))}
              </div>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

function Term({ tone, children }: { tone: 'ok' | 'bad'; children: ReactNode }) {
  return <span className={`${ui.pill} ${tone === 'ok' ? ui.pillOk : ui.pillErr} ${styles.term}`}>{children}</span>
}

function ParsedTable({ parsed: p, hardcodedSubs }: { parsed: ParsedRelease; hardcodedSubs: string | null }) {
  const episodes =
    p.kind === 'Episode'
      ? p.airDate
        ? p.airDate
        : p.fullSeason
          ? `Season ${p.seasonNumber} (pack)`
          : p.absoluteEpisodeNumbers.length > 0
            ? `Absolute ${p.absoluteEpisodeNumbers.join(', ')}`
            : `S${pad(p.seasonNumber ?? 0)}` + p.episodeNumbers.map((e) => `E${pad(e)}`).join('')
      : null

  const quality = p.version > 1 || p.real > 0 ? `${p.quality} (v${p.version}${p.real ? `, REAL ${p.real}` : ''}${p.isRepack ? ', repack' : ''})` : p.quality

  const rows: [string, ReactNode][] = [
    ['Type', <span key="v" className={ui.tag}>{p.kind}</span>],
    ['Title', <b key="v" className={styles.strong}>{p.title}</b>],
    ['Year', p.year > 0 ? String(p.year) : null],
    ['Episodes', episodes && <span key="v" className={ui.mono}>{episodes}</span>],
    ['Quality', <span key="v" className={`${ui.pill} ${ui.pillBusy}`}>{quality}</span>],
    [
      'Languages',
      p.languages.length > 0 ? (
        <span key="v" className={styles.pills}>
          {p.languages.map((l) => (
            <span key={l} className={ui.pill}>
              {l}
            </span>
          ))}
        </span>
      ) : (
        <span key="v" className={ui.muted}>none named</span>
      ),
    ],
    ['Release group', p.releaseGroup && <span key="v" className={ui.tag}>{p.releaseGroup}</span>],
    ['Edition', p.edition],
    ['HDR', p.hdr !== 'None' ? <span key="v" className={`${ui.pill} ${ui.pillWarn}`}>{p.hdr}</span> : null],
    ['Video', p.videoCodec],
    ['Audio', p.audioCodec],
    ['Hardcoded subs', hardcodedSubs && <span key="v" className={`${ui.pill} ${ui.pillWarn}`}>{hardcodedSubs}</span>],
    ['IMDb / TMDB', [p.imdbId, p.tmdbId].filter(Boolean).join(' / ') || null],
    ['Matching key', p.cleanTitle && <span key="v" className={ui.mono}>{p.cleanTitle}</span>],
  ]

  return (
    <section className={ui.panel}>
      <div className={styles.head}>
        <Icon name="list" size={17} className={styles.headIcon} />
        <h2>Parsed</h2>
      </div>
      <dl className={styles.kv}>
        {rows
          .filter(([, value]) => value)
          .map(([label, value]) => (
            <div key={label} className={styles.kvRow}>
              <dt>{label}</dt>
              <dd>{value}</dd>
            </div>
          ))}
      </dl>
    </section>
  )
}

function pad(n: number) {
  return String(n).padStart(2, '0')
}
