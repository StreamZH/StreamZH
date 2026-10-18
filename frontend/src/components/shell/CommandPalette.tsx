import { useQuery } from '@tanstack/react-query'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router'
import { libraryApi, queryKeys } from '../../api/library'
import { tmdbImage } from '../../lib/format'
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
export default function CommandPalette({ onClose }: { onClose: () => void }) {
  const navigate = useNavigate()
  const [text, setText] = useState('')
  const [selected, setSelected] = useState(0)
  const input = useRef<HTMLInputElement>(null)
  const list = useRef<HTMLDivElement>(null)
  const movies = useQuery({ queryKey: queryKeys.movies, queryFn: libraryApi.movies })
  const series = useQuery({ queryKey: queryKeys.series, queryFn: libraryApi.series })

  useEffect(() => input.current?.focus(), [])

  const entries: Entry[] = useMemo(() => {
    const go = (to: string) => () => {
      onClose()
      void navigate(to)
    }
    return [
      ...paletteTargets.map((t) => ({
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
        words: `${s.title} ${s.year} series`.toLowerCase(),
      })),
      ...(movies.data ?? []).map((m) => ({
        key: `movie:${m.id}`,
        label: m.title,
        hint: `Movie · ${m.year}`,
        group: 'Library',
        poster: m.posterPath,
        run: go(`/library/movies/${m.id}`),
        words: `${m.title} ${m.originalTitle} ${m.year} movie film`.toLowerCase(),
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
  }, [movies.data, series.data, navigate, onClose])

  const results = useMemo(() => {
    const words = text.trim().toLowerCase().split(/\s+/).filter(Boolean)
    if (words.length === 0) return entries.filter((e) => e.group !== 'Library').slice(0, 40)
    return entries
      .map((e) => ({ e, s: score(e, words) }))
      .filter((x): x is { e: Entry; s: number } => x.s != null)
      .sort((a, b) => a.s - b.s || a.e.label.localeCompare(b.e.label))
      .slice(0, 50)
      .map((x) => x.e)
  }, [entries, text])

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
            placeholder="Search pages, movies and series …"
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
