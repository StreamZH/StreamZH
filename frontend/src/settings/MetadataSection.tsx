import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { libraryApi, queryKeys } from '../api/library'
import ui from '../components/ui.module.css'
import page from '../pages/Page.module.css'
import styles from '../pages/Settings.module.css'

export function MetadataSection() {
  const queryClient = useQueryClient()
  const settings = useQuery({ queryKey: queryKeys.metadata, queryFn: libraryApi.metadataSettings })
  const [token, setToken] = useState('')
  const [language, setLanguage] = useState<string | null>(null)

  const effectiveLanguage = language ?? settings.data?.language ?? 'en-US'

  const test = useMutation({ mutationFn: () => libraryApi.testMetadata(token.trim() || null) })
  const refreshAll = useMutation({ mutationFn: libraryApi.refreshAll })
  const save = useMutation({
    mutationFn: () => libraryApi.saveMetadataSettings(token.trim() || null, effectiveLanguage),
    onSuccess: async (data) => {
      queryClient.setQueryData(queryKeys.metadata, data)
      setToken('')
      test.reset()
    },
  })

  const submit = (event: FormEvent) => {
    event.preventDefault()
    save.mutate()
  }

  if (!settings.data) return null

  return (
    <form className={page.panel} onSubmit={submit}>
      <h2 className={page.panelTitle}>Metadata (TMDB)</h2>
      <p className={styles.text}>
        StreamZH finds titles, posters and episode lists on The Movie Database. Create a free account on{' '}
        <a href="https://www.themoviedb.org/settings/api" target="_blank" rel="noreferrer">
          themoviedb.org
        </a>{' '}
        and paste the <em>API Read Access Token</em> (or the shorter API key) here.
      </p>
      <div className={styles.grid}>
        <label className={styles.field}>
          <span>
            API token{' '}
            {settings.data.tmdbConfigured ? (
              <span className={`${ui.badge} ${ui.ok}`}>Saved {settings.data.tmdbTokenHint}</span>
            ) : (
              <span className={`${ui.badge} ${ui.missing}`}>Not set</span>
            )}
          </span>
          <input
            type="password"
            autoComplete="off"
            placeholder={settings.data.tmdbConfigured ? 'Leave empty to keep the saved token' : 'eyJhbGciOi…'}
            value={token}
            onChange={(e) => setToken(e.target.value)}
          />
        </label>
        <label className={styles.field}>
          <span>Language for titles and descriptions</span>
          <select value={effectiveLanguage} onChange={(e) => setLanguage(e.target.value)}>
            {settings.data.languages.map((l) => (
              <option key={l} value={l}>
                {l}
              </option>
            ))}
          </select>
        </label>
      </div>
      <div className={styles.actions}>
        <button className={styles.primary} type="submit" disabled={save.isPending || (!token.trim() && effectiveLanguage === settings.data.language)}>
          {save.isPending ? 'Saving…' : 'Save'}
        </button>
        <button
          className={styles.secondary}
          type="button"
          onClick={() => test.mutate()}
          disabled={test.isPending || (!token.trim() && !settings.data.tmdbConfigured)}
        >
          {test.isPending ? 'Testing…' : 'Test connection'}
        </button>
        {test.isSuccess && <span className={styles.ok}>TMDB connection works</span>}
        {test.error && <span className={styles.error}>{test.error.message}</span>}
        {save.isSuccess && !test.isSuccess && <span className={styles.ok}>Saved</span>}
        {save.error && <span className={styles.error}>{save.error.message}</span>}
      </div>
      <div className={styles.actions}>
        <button className={styles.secondary} type="button" onClick={() => refreshAll.mutate()} disabled={refreshAll.isPending || !settings.data.tmdbConfigured}>
          Refresh all metadata
        </button>
        <span className={styles.muted}>
          {refreshAll.isSuccess
            ? `${refreshAll.data.queued} titles queued, progress shows in the sidebar.`
            : 'Loads titles, posters and episodes again for the whole library. Happens automatically when the language changes.'}
        </span>
      </div>
      <p className={styles.attribution}>
        This product uses the TMDB API but is not endorsed or certified by TMDB. Swiss and Austrian variants use the German titles, since TMDB has few
        regional translations.
      </p>
    </form>
  )
}
