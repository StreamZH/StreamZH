import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { importListApi, importListKeys } from '../api/importLists'
import { useAuth } from '../auth/auth'
import ui from '../components/ui.module.css'
import { Section } from '../settings/SettingsKit'
import { mediaLabel, tmdbUrl } from './labels'
import styles from './ImportLists.module.css'

const pageSize = 50

/** Movies and series no list may add (Radarr's "List exclusions"), with search and remove. */
export function ExclusionsSection() {
  const { user } = useAuth()
  const canEdit = user?.role === 'Owner' || user?.role === 'Admin'
  const queryClient = useQueryClient()
  const exclusions = useQuery({ queryKey: importListKeys.exclusions, queryFn: importListApi.exclusions })
  const [search, setSearch] = useState('')
  const [shown, setShown] = useState(pageSize)
  const remove = useMutation({
    mutationFn: importListApi.removeExclusion,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: importListKeys.exclusions }),
  })

  const term = search.trim().toLowerCase()
  const all = exclusions.data ?? []
  const matches = all.filter((e) => !term || e.title.toLowerCase().includes(term) || String(e.tmdbId) === term || String(e.year) === term)

  return (
    <Section
      id="exclusions"
      title="Exclusions"
      description="Lists never add these. Exclude items from a list's Preview, or when you remove something a list added."
    >
      {exclusions.isError && <p className={ui.error}>{exclusions.error.message}</p>}
      {all.length === 0 ? (
        exclusions.isSuccess && <p className={ui.muted}>No exclusions.</p>
      ) : (
        <>
          <div className={styles.toolbar}>
            <input
              className={`${ui.input} ${styles.search}`}
              type="search"
              placeholder="Search title, year or TMDB id"
              aria-label="Search exclusions"
              value={search}
              onChange={(e) => {
                setSearch(e.target.value)
                setShown(pageSize)
              }}
            />
            <span className={styles.note}>
              {matches.length === all.length ? `${all.length} excluded` : `${matches.length} of ${all.length}`}
            </span>
          </div>
          <table className={styles.table}>
            <thead>
              <tr>
                <th>Title</th>
                <th className={styles.hideNarrow}>Type</th>
                <th className={styles.hideNarrow}>TMDB</th>
                <th aria-label="Actions" />
              </tr>
            </thead>
            <tbody>
              {matches.slice(0, shown).map((e) => (
                <tr key={e.id}>
                  <td className={styles.titleCell}>
                    {e.title} {e.year > 0 && <span className={styles.year}>({e.year})</span>}
                  </td>
                  <td className={styles.hideNarrow}>{e.mediaType === 'Series' ? 'Series' : 'Movie'}</td>
                  <td className={styles.hideNarrow}>
                    <a href={tmdbUrl(e.mediaType, e.tmdbId)} target="_blank" rel="noreferrer" title={`${mediaLabel(e.mediaType)} on TMDB`}>
                      {e.tmdbId}
                    </a>
                  </td>
                  <td>
                    {canEdit && (
                      <button className={`${ui.button} ${styles.small}`} type="button" onClick={() => remove.mutate(e.id)} disabled={remove.isPending}>
                        Remove
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {matches.length > shown && (
            <button className={`${ui.button} ${styles.more}`} type="button" onClick={() => setShown(shown + pageSize)}>
              Show {Math.min(pageSize, matches.length - shown)} more
            </button>
          )}
          {remove.error && <p className={ui.error}>{remove.error.message}</p>}
        </>
      )}
    </Section>
  )
}
