import { useQuery } from '@tanstack/react-query'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router'
import { alternativeTitlesApi } from '../../api/alternativeTitles'
import { libraryApi, queryKeys } from '../../api/library'
import { tmdbImage } from '../../lib/format'
import { requesterMayOpen } from '../../requests/roles'
import { toggleTheme } from '../../lib/theme'
import Icon, { type IconName } from '../Icon'
import { paletteTargets } from './nav'
import styles from './Shell.module.css'

interface Entry {
  key: string
  label: string
  hint: string
  group: string
  icon?: IconName
  poster?: string | null
  run: () => void
  words: string
}

/** Ranks how well the words match: start of the label first, then words, then anywhere. Null when a word is missing. */
function score(entry: Entry, words: string[]): number | null {
  const label = entry.label.toLowerCase()
  let total = 0
  for (const w of words) {
    if (label.startsWith(w)) total += 0
    else if (label.includes(` ${w}`)) total += 1
    else if (label.includes(w)) total += 2
    else if (entry.words.includes(w)) total += 4
    else return null
  }
  return total
}

/**
 * «Search or jump to» (Ctrl K / ⌘ K), like CloudZH: every page, every movie and series of the library and a few
 * actions, filtered while typing; arrow keys and Enter.
 */
export default function CommandPalette({ onClose, requester = false }: { onClose: () => void; requester?: boolean }) {
  const navigate = useNavigate()
  const [text, setText] = useState('')
  const [selected, setSelected] = useState(0)
  const input = useRef<HTMLInputElement>(null)
  const list = useRef<HTMLDivElement>(null)
  // A requester (0.10) jumps only to their pages and has no library to search.
  const movies = useQuery({ queryKey: queryKeys.movies, queryFn: libraryApi.movies, enabled: !requester })
  const series = useQuery({ queryKey: queryKeys.series, queryFn: libraryApi.series, enabled: !requester })
  const titles = useQuery({ queryKey: ['library-titles'], queryFn: alternativeTitlesApi.all, staleTime: 60_000, enabled: !requester })

  useEffect(() => input.current?.focus(), [])

  const entries: Entry[] = useMemo(() => {
    // Alternative titles ("Die Simpsons", "Haus des Geldes") find the item too.
    const known = new Map<string, string>()
    for (const t of titles.data ?? []) known.set(`${t.mediaType}:${t.id}`, `${known.get(`${t.mediaType}:${t.id}`) ?? ''} ${t.title}`)
    const aka = (type: string, id: number) => known.get(`${type}:${id}`) ?? ''
    const go = (to: string) => () => {
      onClose()
      void navigate(to)
    }
    return [
      ...paletteTargets.filter((t) => !requester || requesterMayOpen(t.to)).map((t) => ({
        key: `page:${t.to}`,
        label: t.label,
        hint: t.group,
        group: 'Pages',
        icon: t.icon,
        run: go(t.to),
        words: `${t.label} ${t.group} ${t.keywords}`.toLowerCase(),
      })),
      ...(series.data ?? []).map((s) => ({
        key: `series:${s.id}`,
        label: s.title,
        hint: `Series · ${s.year}`,
        group: 'Library',
        poster: s.posterPath,
        run: go(`/library/series/${s.id}`),
        words: `${s.title} ${s.year} series ${aka('series', s.id)}`.toLowerCase(),
      })),
      ...(movies.data ?? []).map((m) => ({
        key: `movie:${m.id}`,
        label: m.title,
        hint: `Movie · ${m.year}`,
        group: 'Library',
        poster: m.posterPath,
        run: go(`/library/movies/${m.id}`),
        words: `${m.title} ${m.originalTitle} ${m.year} movie film ${aka('movie', m.id)}`.toLowerCase(),
      })),
      {
        key: 'act:theme',
        label: 'Light or dark mode',
        hint: 'Appearance',
        group: 'Actions',
        icon: 'moon',
        run: () => {
          toggleTheme()
          onClose()
        },
        words: 'theme dark light appearance colour color',
      },
    ]
  }, [movies.data, series.data, titles.data, navigate, onClose, requester])

  const results = useMemo(() => {
    const words = text.trim().toLowerCase().split(/\s+/).filter(Boolean)
    if (words.length === 0) return entries.filter((e) => e.group !== 'Library').slice(0, 40)
    // Last: look the words up on Discover (TMDB), to request something that is not in the library yet.
    const discover: Entry = {
      key: 'act:discover',
      label: `Search Discover for “${text.trim()}”`,
      hint: 'Movies and series to request',
      group: 'Discover',
      icon: 'compass',
      run: () => {
        onClose()
        void navigate(`/discover?q=${encodeURIComponent(text.trim())}`)
      },
      words: '',
    }
    return [
      ...entries
        .map((e) => ({ e, s: score(e, words) }))
        .filter((x): x is { e: Entry; s: number } => x.s != null)
        .sort((a, b) => a.s - b.s || a.e.label.localeCompare(b.e.label))
        .slice(0, 49)
        .map((x) => x.e),
      discover,
    ]
  }, [entries, text, navigate, onClose])

  useEffect(() => {
    list.current?.querySelector(`[data-index="${selected}"]`)?.scrollIntoView({ block: 'nearest' })
  }, [selected])

  const onKey = (e: React.KeyboardEvent) => {
    if (e.key === 'ArrowDown') {
      e.preventDefault()
      setSelected((i) => Math.min(results.length - 1, i + 1))
    } else if (e.key === 'ArrowUp') {
      e.preventDefault()
      setSelected((i) => Math.max(0, i - 1))
    } else if (e.key === 'Enter') {
      e.preventDefault()
      results[selected]?.run()
    } else if (e.key === 'Escape') {
      e.preventDefault()
      onClose()
    }
  }

  return (
    <div className={styles.palette} role="dialog" aria-modal="true" aria-label="Search or jump to">
      <div className={styles.scrim} onClick={onClose} />
      <div className={styles.paletteBox} onKeyDown={onKey}>
        <div className={styles.pin}>
          <Icon name="search" size={18} />
          <input
            ref={input}
            value={text}
            onChange={(e) => {
              setText(e.target.value)
              setSelected(0)
            }}
            placeholder={requester ? 'Search pages or titles to request …' : 'Search pages, movies and series …'}
            aria-label="Search"
            aria-controls="palette-list"
            aria-activedescendant={results[selected] ? `palette-${selected}` : undefined}
            spellCheck={false}
            autoComplete="off"
          />
          <kbd className={styles.kbd}>Esc</kbd>
        </div>
        <div className={styles.plist} id="palette-list" role="listbox" ref={list}>
          {results.length === 0 && <p className={styles.pempty}>Nothing found for «{text}».</p>}
          {results.map((r, i) => {
            const head = i === 0 || results[i - 1].group !== r.group ? r.group : null
            const poster = r.poster !== undefined ? tmdbImage(r.poster, 'w92') : null
            return (
              <div key={r.key}>
                {head && <div className={styles.pgroup}>{head}</div>}
                <button
                  type="button"
                  id={`palette-${i}`}
                  data-index={i}
                  role="option"
                  aria-selected={i === selected}
                  className={`${styles.pitem} ${i === selected ? styles.psel : ''}`}
                  onMouseMove={() => setSelected(i)}
                  onClick={() => r.run()}
                >
                  {r.poster !== undefined ? (
                    poster ? <img className={styles.pthumb} src={poster} alt="" loading="lazy" /> : <span className={styles.pthumb} />
                  ) : (
                    <Icon name={r.icon ?? 'right'} size={17} />
                  )}
                  <span>{r.label}</span>
                  <small>{r.hint}</small>
                </button>
              </div>
            )
          })}
        </div>
        <div className={styles.pfoot}>
          <span>↑ ↓ choose</span>
          <span>↵ open</span>
          <span>Esc close</span>
        </div>
      </div>
    </div>
  )
}
