import { useQuery } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { libraryApi, queryKeys } from '../api/library'
import { parseRelease, type ParsedRelease } from '../api/parse'
import ui from '../components/ui.module.css'
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
  const [submitted, setSubmitted] = useState<{ title: string; target: string; size: number | null } | null>(null)

  const movies = useQuery({ queryKey: queryKeys.movies, queryFn: libraryApi.movies })
  const series = useQuery({ queryKey: queryKeys.series, queryFn: libraryApi.series })

  const result = useQuery({
    queryKey: ['parse', submitted],
    queryFn: () => parseRelease(submitted!.title, submitted!.target, submitted!.size),
    enabled: !!submitted?.title,
  })

  const submit = (event: FormEvent) => {
    event.preventDefault()
    if (title.trim()) setSubmitted({ title: title.trim(), target, size: Number(sizeMb) || null })
  }

  return (
    <div className={page.page}>
      <header>
        <Link className={styles.back} to="/settings">
          ← Settings
        </Link>
        <h1 className={page.title}>Release test</h1>
        <p className={page.lead}>Paste a release name to see how StreamZH reads it and whether it would grab it.</p>
      </header>

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
          <p className={ui.muted}>Profile: {result.data.decision.profile}</p>
          {result.data.decision.rejections.length > 0 && (
            <ul className={styles.reasons}>
              {result.data.decision.rejections.map((r) => (
                <li key={r.code}>{r.message}</li>
              ))}
            </ul>
          )}
        </section>
      )}
      {result.data?.parsed && <ParsedTable parsed={result.data.parsed} />}
    </div>
  )
}

function ParsedTable({ parsed: p }: { parsed: ParsedRelease }) {
  const episodes =
    p.kind === 'Episode'
      ? p.airDate
        ? p.airDate
        : p.fullSeason
          ? `Season ${p.seasonNumber} (pack)`
          : p.absoluteEpisodeNumbers.length > 0
            ? `Absolute ${p.absoluteEpisodeNumbers.join(', ')}`
            : `S${String(p.seasonNumber ?? 0).padStart(2, '0')} E${p.episodeNumbers.join(', E')}`
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
