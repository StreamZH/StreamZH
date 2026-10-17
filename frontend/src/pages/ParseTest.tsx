import { useQuery } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { formatScore } from '../api/indexerOptions'
import { indexerKeys, indexersApi } from '../api/indexers'
import { libraryApi, queryKeys } from '../api/library'
import { parseRelease, type ParsedRelease, type ParseResult } from '../api/parse'
import ui from '../components/ui.module.css'
import { SettingsPage } from '../settings/SettingsKit'
import page from './Page.module.css'
import styles from './ParseTest.module.css'

const examples = [
  'Dune.Part.Two.2024.German.DL.2160p.WEB.H265-GROUP',
  'Interstellar.2014.German.BDRip.DL.1080p.x265.AAC-2BA',
  'Jackass.2026.GERMAN.CAM.1080p.x264-LiNEUP',
  'Breaking.Bad.S01E02.German.DL.720p.WEB.h264-GRP',
]

/** Shows how StreamZH reads a release name and whether it would grab it. */
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

  return (
    <SettingsPage title="Release test" lead="Paste a release name to see how StreamZH reads it and whether it would grab it.">

      <form className={page.panel} onSubmit={submit}>
        <label className={ui.field}>
          <span>Release name</span>
          <input className={`${ui.input} ${ui.mono}`} value={title} onChange={(e) => setTitle(e.target.value)} spellCheck={false} autoFocus />
        </label>
        <div className={styles.examples}>
          {examples.map((e) => (
            <button key={e} type="button" className={styles.example} onClick={() => setTitle(e)}>
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
            <input className={ui.input} inputMode="decimal" value={sizeMb} onChange={(e) => setSizeMb(e.target.value)} placeholder="e.g. 8500" />
          </label>
          <button className={ui.primary} type="submit" disabled={!title.trim()}>
            Test
          </button>
        </div>
      </form>

      {result.isError && <p className={ui.error}>{result.error.message}</p>}
      {result.data && !result.data.parsed && <p className={ui.muted}>StreamZH could not read this name.</p>}
      {result.data?.decision && (
        <section className={`${page.panel} ${result.data.decision.accepted ? styles.accepted : styles.rejected}`}>
          <h2 className={page.panelTitle}>
            {result.data.decision.accepted ? 'Would be grabbed' : 'Would be rejected'} for {result.data.decision.target}
          </h2>
          <p className={ui.muted}>
            Profile: {result.data.decision.profile}
            {result.data.releaseProfiles.length > 0 && <> · Preferred score {formatScore(result.data.decision.preferredScore)}</>}
          </p>
          {result.data.decision.rejections.length > 0 && (
            <ul className={styles.reasons}>
              {result.data.decision.rejections.map((r) => (
                <li key={r.code}>{r.message}</li>
              ))}
            </ul>
          )}
        </section>
      )}
      {result.data?.parsed && <ReleaseProfiles result={result.data} />}
      {result.data?.parsed && <ParsedTable parsed={result.data.parsed} hardcodedSubs={result.data.hardcodedSubs} />}
    </SettingsPage>
  )
}

/** Which release profiles match the name, with the terms found and the preferred score. */
function ReleaseProfiles({ result }: { result: ParseResult }) {
  const matches = result.releaseProfiles
  return (
    <section className={page.panel}>
      <h2 className={page.panelTitle}>
        Release profiles
        {matches.length > 0 && <span className={styles.score}>Preferred score {formatScore(result.preferredScore)}</span>}
      </h2>
      {matches.length === 0 ? (
        <p className={ui.muted}>
          No release profile matches this name. Release profiles are set up under <Link to="/settings/indexers#release-profiles">Settings → Indexers</Link>.
        </p>
      ) : (
        <ul className={styles.profiles}>
          {matches.map((m) => (
            <li key={m.id} className={m.rejects ? styles.profileRejects : undefined}>
              <strong>{m.name}</strong>
              {m.limited && <span className={ui.muted}> (only some indexers or quality profiles)</span>}
              <ul className={styles.terms}>
                {m.missingRequired && <li className={styles.bad}>Contains none of {m.required.map((t) => `'${t}'`).join(', ')}</li>}
                {m.requiredMatched.length > 0 && <li>Contains {m.requiredMatched.map((t) => `'${t}'`).join(', ')}</li>}
                {m.ignoredMatched.length > 0 && <li className={styles.bad}>Contains {m.ignoredMatched.map((t) => `'${t}'`).join(', ')} (must not)</li>}
                {m.preferredMatched.map((p) => (
                  <li key={p.term}>
                    Preferred '{p.term}' <span className={p.score > 0 ? styles.plus : styles.bad}>{formatScore(p.score)}</span>
                  </li>
                ))}
              </ul>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
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

  const rows: [string, string | null][] = [
    ['Type', p.kind],
    ['Title', p.title],
    ['Year', p.year > 0 ? String(p.year) : null],
    ['Episodes', episodes],
    ['Quality', p.version > 1 || p.real > 0 ? `${p.quality} (v${p.version}${p.real ? `, REAL ${p.real}` : ''}${p.isRepack ? ', repack' : ''})` : p.quality],
    ['Languages', p.languages.length > 0 ? p.languages.join(', ') : 'none named'],
    ['Release group', p.releaseGroup],
    ['Edition', p.edition],
    ['HDR', p.hdr !== 'None' ? p.hdr : null],
    ['Video', p.videoCodec],
    ['Audio', p.audioCodec],
    ['Hardcoded subs', hardcodedSubs],
    ['IMDb / TMDB', [p.imdbId, p.tmdbId].filter(Boolean).join(' / ') || null],
    ['Matching key', p.cleanTitle],
  ]

  return (
    <section className={page.panel}>
      <h2 className={page.panelTitle}>Parsed</h2>
      <dl className={styles.facts}>
        {rows
          .filter(([, value]) => value)
          .map(([label, value]) => (
            <div key={label} className={styles.fact}>
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
