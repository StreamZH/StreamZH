import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { ApiError } from '../api/client'
import { builtInImplementation, downloadClientKeys, downloadClientsApi } from '../api/downloadClients'
import { libraryApi, queryKeys, type MediaType } from '../api/library'
import { metadataApi, metadataKeys } from '../api/metadata'
import { profileKeys } from '../api/profiles'
import { setupGuideApi, setupGuideKeys, type LanguageOption, type ReleaseLanguageChoice, type SetupGuide } from '../api/setupGuide'
import { outsideStorageCode, storageApi, storageKeys } from '../api/storage'
import { useTurnOnBuiltIn } from '../downloadClients/useBuiltIn'
import FolderPicker from '../components/FolderPicker'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import { examplePath } from './useSetupGuide'
import styles from './Welcome.module.css'

/** After a change: the checklist and the lists it was made from are read again. */
function useRefresh() {
  const queryClient = useQueryClient()
  return (...keys: (readonly unknown[])[]) => {
    void queryClient.invalidateQueries({ queryKey: setupGuideKeys.guide })
    for (const key of keys) void queryClient.invalidateQueries({ queryKey: key })
  }
}

function errorText(error: Error | null, field?: string): string | null {
  if (!error) return null
  if (error instanceof ApiError) return (field ? error.fieldErrors[field]?.[0] : undefined) ?? Object.values(error.fieldErrors)[0]?.[0] ?? error.message
  return error.message
}

/** A folder field with "Browse…" (the folder picker shows the server's folders, also in Docker). */
function FolderField({ value, onChange, label, placeholder }: { value: string; onChange: (path: string) => void; label: string; placeholder: string }) {
  const [picking, setPicking] = useState(false)
  return (
    <>
      <input
        className={`${ui.input} ${ui.mono}`}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        aria-label={label}
        spellCheck={false}
      />
      <button className={ui.button} type="button" onClick={() => setPicking(true)}>
        <Icon name="folder" size={15} />
        Browse…
      </button>
      {picking && (
        <FolderPicker
          initialPath={value}
          onClose={() => setPicking(false)}
          onSelect={(selected) => {
            onChange(selected)
            setPicking(false)
          }}
        />
      )}
    </>
  )
}

/** Step 1 (owner): add a storage location. */
export function StorageForm({ guide }: { guide: SetupGuide }) {
  const refresh = useRefresh()
  const [path, setPath] = useState('')
  const add = useMutation({
    mutationFn: () => storageApi.add(path.trim()),
    onSuccess: () => {
      setPath('')
      refresh(storageKeys.storage)
    },
  })
  const submit = (event: FormEvent) => {
    event.preventDefault()
    if (path.trim()) add.mutate()
  }
  return (
    <>
      <form className={styles.form} onSubmit={submit}>
        <FolderField value={path} onChange={setPath} label="Storage location" placeholder={examplePath(guide, 'storage')} />
        <button className={ui.primary} type="submit" disabled={!path.trim() || add.isPending}>
          <Icon name="plus" size={15} />
          Add location
        </button>
      </form>
      {add.error && (
        <p className={ui.error} role="alert">
          {errorText(add.error, 'path')}
        </p>
      )}
    </>
  )
}

/** Step 2: add a root folder for movies or series; a folder outside the storage locations can be allowed by the owner in one click. */
export function RootFolderForm({ guide }: { guide: SetupGuide }) {
  const refresh = useRefresh()
  const [path, setPath] = useState('')
  const [mediaType, setMediaType] = useState<MediaType>(guide.details.movieRootFolders > 0 && guide.details.seriesRootFolders === 0 ? 'Series' : 'Movie')
  const add = useMutation({
    mutationFn: () => libraryApi.addRootFolder(path.trim(), mediaType),
    onSuccess: () => {
      setPath('')
      refresh(queryKeys.rootFolders, storageKeys.storage)
    },
  })
  const outside = add.error instanceof ApiError && add.error.code === outsideStorageCode
  const allow = useMutation({
    mutationFn: () => storageApi.add(path.trim()),
    onSuccess: () => add.mutate(),
  })
  const submit = (event: FormEvent) => {
    event.preventDefault()
    if (path.trim()) add.mutate()
  }
  return (
    <>
      <form className={styles.form} onSubmit={submit}>
        <FolderField value={path} onChange={setPath} label="Root folder" placeholder={examplePath(guide, mediaType === 'Movie' ? 'movies' : 'series')} />
        <select className={ui.select} value={mediaType} onChange={(e) => setMediaType(e.target.value as MediaType)} aria-label="Media type">
          <option value="Movie">Movies</option>
          <option value="Series">Series</option>
        </select>
        <button className={ui.primary} type="submit" disabled={!path.trim() || add.isPending}>
          <Icon name="plus" size={15} />
          Add folder
        </button>
      </form>
      {outside && guide.isOwner ? (
        <div className={`${ui.infoBox} ${styles.allow}`} role="status">
          <span>
            <code>{path.trim()}</code> is outside your storage locations.
          </span>
          <button className={`${ui.primary} ${ui.sm}`} type="button" onClick={() => allow.mutate()} disabled={allow.isPending || add.isPending}>
            <Icon name="check" size={14} />
            Allow and add
          </button>
          {allow.error && <span className={ui.error}>{errorText(allow.error, 'path')}</span>}
        </div>
      ) : (
        add.error && (
          <p className={ui.error} role="alert">
            {outside ? 'This folder is outside the storage locations. Ask the owner to add it as a storage location first.' : errorText(add.error, 'path')}
          </p>
        )
      )}
    </>
  )
}

/** Step 3: the TMDB key, tested before it is saved. */
export function TmdbForm() {
  const refresh = useRefresh()
  const [token, setToken] = useState('')
  const settings = useQuery({ queryKey: metadataKeys.settings, queryFn: metadataApi.settings })
  const save = useMutation({
    mutationFn: async () => {
      const key = token.trim()
      await metadataApi.test(key)
      const current = settings.data ?? (await metadataApi.settings())
      return metadataApi.save({
        tmdbToken: key,
        language: current.language,
        certificationCountry: current.certificationCountry,
        nfoOriginalTitle: current.nfoOriginalTitle,
        imageCache: current.imageCache,
      })
    },
    onSuccess: () => {
      setToken('')
      refresh(metadataKeys.settings)
    },
  })
  const submit = (event: FormEvent) => {
    event.preventDefault()
    if (token.trim()) save.mutate()
  }
  return (
    <>
      <form className={styles.form} onSubmit={submit}>
        <input
          className={`${ui.input} ${ui.mono}`}
          type="password"
          value={token}
          onChange={(e) => setToken(e.target.value)}
          placeholder="API Read Access Token or API key"
          aria-label="TMDB API key"
          autoComplete="off"
          spellCheck={false}
        />
        <button className={ui.primary} type="submit" disabled={!token.trim() || save.isPending}>
          <Icon name="check" size={15} />
          {save.isPending ? 'Checking…' : 'Check and save'}
        </button>
      </form>
      {save.error && (
        <p className={ui.error} role="alert">
          {errorText(save.error, 'tmdbToken')}
        </p>
      )}
    </>
  )
}

/** Step 5: a Usenet server, the built-in torrent engine (one click) or a torrent client. */
export function DownloadActions({ guide }: { guide: SetupGuide }) {
  const refresh = useRefresh()
  const schema = useQuery({ queryKey: downloadClientKeys.schema, queryFn: downloadClientsApi.schema, staleTime: 10 * 60_000 })
  const clients = useQuery({ queryKey: downloadClientKeys.list, queryFn: downloadClientsApi.list })
  const builtIn = schema.data?.find((t) => t.implementation === builtInImplementation) ?? null
  const turnOn = useTurnOnBuiltIn(builtIn, clients.data ?? [])
  const hasBuiltIn = guide.details.builtInTorrent || (clients.data ?? []).some((c) => c.implementation === builtInImplementation)

  return (
    <>
      <div className={styles.actions}>
        <Link className={guide.details.usenetServers > 0 ? ui.button : ui.primary} to="/settings/usenet">
          <Icon name="server" size={15} />
          Add a Usenet server
        </Link>
        {hasBuiltIn ? (
          <span className={styles.ok}>
            <Icon name="check" size={15} />
            Built-in torrent engine is on
          </span>
        ) : (
          <button
            className={ui.button}
            type="button"
            disabled={!builtIn || turnOn.isPending}
            onClick={() => turnOn.mutate(undefined, { onSuccess: () => refresh(downloadClientKeys.all) })}
          >
            <Icon name="zap" size={15} />
            {turnOn.isPending ? 'Turning on…' : 'Use the built-in torrent engine'}
          </button>
        )}
        <Link className={ui.ghost} to="/settings/downloadclients">
          qBittorrent, Transmission or Deluge
          <Icon name="right" size={14} />
        </Link>
      </div>
      {turnOn.error && (
        <p className={ui.error} role="alert">
          {errorText(turnOn.error)}
        </p>
      )}
    </>
  )
}

interface LanguageCard {
  key: string
  title: string
  text: string
  choice: ReleaseLanguageChoice
}

const anyOrOriginal: LanguageCard[] = [
  { key: 'any', title: 'Any language', text: 'The best release in whatever language it comes.', choice: { language: 'any' } },
  {
    key: 'original',
    title: 'Original language',
    text: "The title's own language, e.g. English for most Hollywood movies.",
    choice: { language: 'original' },
  },
]

function languageCard({ code, name }: LanguageOption): LanguageCard {
  return {
    key: `native:${code}`,
    title: name,
    text: `${name} audio; releases with ${name} and the original language (dual language) preferred.`,
    choice: { language: 'native', code },
  }
}

/**
 * Any, original, then the language already chosen (e.g. German on an existing installation) and the user's own
 * language (browser, else the TMDB language; never English, which "Original language" covers for most titles).
 */
function languageCards(guide: SetupGuide): LanguageCard[] {
  const d = guide.details
  const own: LanguageOption[] = []
  if ((d.releaseLanguage === 'german' || d.releaseLanguage === 'native') && d.releaseLanguageCode && d.releaseLanguageName) {
    own.push({ code: d.releaseLanguageCode, name: d.releaseLanguageName })
  }
  if (d.suggestedLanguage && !own.some((l) => l.name === d.suggestedLanguage?.name)) own.push(d.suggestedLanguage)
  return [...anyOrOriginal, ...own.map(languageCard)]
}

/** The card of the current choice: any, original, or the language of the default profile. */
function chosenKey(guide: SetupGuide): string | null {
  const d = guide.details
  if (!guide.steps.find((s) => s.key === 'language')?.done) return null
  if (d.releaseLanguage === 'any' || d.releaseLanguage === 'original') return d.releaseLanguage
  return d.releaseLanguageCode && (d.releaseLanguage === 'german' || d.releaseLanguage === 'native') ? `native:${d.releaseLanguageCode}` : null
}

/** Step 4: the release language, which picks the default quality profile (HD-1080p, Deutsch 1080p or "<Language> 1080p"). */
export function LanguageChoice({ guide }: { guide: SetupGuide }) {
  const queryClient = useQueryClient()
  const save = useMutation({
    mutationFn: setupGuideApi.language,
    onSuccess: (data) => {
      queryClient.setQueryData(setupGuideKeys.guide, data)
      void queryClient.invalidateQueries({ queryKey: profileKeys.profiles })
      void queryClient.invalidateQueries({ queryKey: profileKeys.settings })
    },
  })
  const current = chosenKey(guide)
  return (
    <>
      <div className={styles.choices} role="radiogroup" aria-label="Release language">
        {languageCards(guide).map((l) => (
          <button
            key={l.key}
            type="button"
            role="radio"
            aria-checked={current === l.key}
            className={`${styles.choice} ${current === l.key ? styles.choiceOn : ''}`}
            onClick={() => save.mutate(l.choice)}
            disabled={save.isPending}
          >
            <b>{l.title}</b>
            <span>{l.text}</span>
          </button>
        ))}
      </div>
      {save.error && (
        <p className={ui.error} role="alert">
          {errorText(save.error, 'language')}
        </p>
      )}
    </>
  )
}
