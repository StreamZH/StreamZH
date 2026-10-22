import { useState, type ReactNode } from 'react'
import { Link, Navigate } from 'react-router'
import type { SetupGuide, SetupStep, SetupStepKey } from '../api/setupGuide'
import Icon, { type IconName } from '../components/Icon'
import { Skeleton } from '../components/Loader'
import PageHeader from '../components/PageHeader'
import ui from '../components/ui.module.css'
import { useIsAdmin } from '../status/useOngoing'
import { DownloadActions, LanguageChoice, RootFolderForm, StorageForm, TmdbForm } from './StepForms'
import { examplePath, nextStep, stepTitles, useDismissGuide, useSetupGuide } from './useSetupGuide'
import styles from './Welcome.module.css'

const tmdbKeyUrl = 'https://www.themoviedb.org/settings/api'

const languageNames = { any: 'Any language', original: 'Original language' } as const

function plural(count: number, one: string, many: string) {
  return `${count} ${count === 1 ? one : many}`
}

/**
 * "Get started" (1.0 RC): what StreamZH needs before it can find and download, as a checklist. Shown right after the
 * owner account is created and from the Overview until it is done or hidden. Owners and admins only.
 */
export default function GetStartedPage() {
  const admin = useIsAdmin()
  const guide = useSetupGuide()
  const dismiss = useDismissGuide()
  if (!admin) return <Navigate to="/" replace />

  const g = guide.data
  const actions = g ? (
    g.dismissed ? (
      <button className={ui.button} type="button" onClick={() => dismiss.mutate(false)} disabled={dismiss.isPending}>
        <Icon name="eye" size={15} />
        Show on Overview
      </button>
    ) : (
      <button className={ui.ghost} type="button" onClick={() => dismiss.mutate(true)} disabled={dismiss.isPending} title="The Overview no longer shows the setup progress">
        <Icon name="eyeoff" size={15} />
        Hide this guide
      </button>
    )
  ) : undefined

  return (
    <div className={styles.page}>
      <PageHeader
        title="Get started"
        lead="What StreamZH needs before it can find and download for you. Each step ticks itself off once it is set up."
        actions={actions}
      />
      {guide.isError && <p className={ui.errBox}>{guide.error.message}</p>}
      {!g ? guide.isError ? null : <Skeleton rows={6} /> : <Guide guide={g} />}
    </div>
  )
}

function Guide({ guide }: { guide: SetupGuide }) {
  const next = nextStep(guide)
  const [open, setOpen] = useState<SetupStepKey | null>(next)

  // A step that gets done while it is open closes, and the next one opens.
  const doneKeys = guide.steps.filter((s) => s.done).map((s) => s.key).join(',')
  const [seenDone, setSeenDone] = useState(doneKeys)
  if (seenDone !== doneKeys) {
    setSeenDone(doneKeys)
    if (open && guide.steps.find((s) => s.key === open)?.done && !seenDone.split(',').includes(open)) setOpen(next)
  }
  const percent = guide.total > 0 ? (guide.done / guide.total) * 100 : 0

  return (
    <>
      {guide.complete ? (
        <section className={`${ui.panel} ${styles.ready}`} aria-label="Ready">
          <span className={styles.readyIcon} aria-hidden="true">
            <Icon name="check" size={20} />
          </span>
          <div className={styles.migrateText}>
            <h2>StreamZH is ready</h2>
            <p>Find a movie or series to add, or import the ones you already have in your root folders.</p>
          </div>
          <div className={styles.actions}>
            <Link className={ui.button} to="/library/import">
              <Icon name="folderplus" size={15} />
              Import library
            </Link>
            <Link className={ui.primary} to="/discover">
              <Icon name="compass" size={15} />
              Discover
            </Link>
          </div>
        </section>
      ) : (
        <section className={`${ui.panel} ${styles.progress}`} aria-label="Progress">
          <span className={styles.count}>
            {guide.done} of {guide.total}
            <small>done</small>
          </span>
          <p className={styles.progressText}>{next ? <>Next: {stepTitles[next]}.</> : 'Almost there.'}</p>
          <div
            className={styles.bar}
            role="progressbar"
            aria-label="Setup progress"
            aria-valuemin={0}
            aria-valuemax={guide.total}
            aria-valuenow={guide.done}
          >
            <i style={{ width: `${percent}%` }} />
          </div>
        </section>
      )}

      {!guide.complete && !guide.details.migrated && (
        <section className={`${ui.panel} ${styles.migrate}`} aria-label="Migration">
          <span className={styles.migrateIcon} aria-hidden="true">
            <Icon name="mountin" size={20} />
          </span>
          <div className={styles.migrateText}>
            <h2>Coming from Radarr, Sonarr, Prowlarr or SABnzbd?</h2>
            <p>Take over your folders, indexers, download clients and library in a few steps instead. The checklist below fills itself as you go.</p>
          </div>
          <Link className={ui.primary} to="/settings/migration">
            Start migration
            <Icon name="right" size={15} />
          </Link>
        </section>
      )}

      <section className={`${ui.panel} ${styles.steps}`} aria-label="Setup steps">
        <ol>
          {guide.steps.map((step, index) => (
            <Step
              key={step.key}
              index={index + 1}
              step={step}
              guide={guide}
              next={step.key === next}
              open={open === step.key}
              onToggle={() => setOpen(open === step.key ? null : step.key)}
            />
          ))}
        </ol>
      </section>

      <Extras guide={guide} />
    </>
  )
}

function Step(props: { index: number; step: SetupStep; guide: SetupGuide; next: boolean; open: boolean; onToggle: () => void }) {
  const { step, guide } = props
  const askOwner = step.key === 'storage' && !step.done && !guide.isOwner
  const id = `setup-step-${step.key}`
  const state = step.done ? (
    <span className={`${ui.pill} ${ui.pillOk}`}>Done</span>
  ) : askOwner ? (
    <span className={`${ui.pill} ${ui.pillWarn}`}>Ask the owner</span>
  ) : (
    <span className={ui.pill}>To do</span>
  )

  return (
    <li className={`${styles.step} ${props.open ? styles.open : ''}`}>
      <button type="button" className={styles.stepHead} onClick={props.onToggle} aria-expanded={props.open} aria-controls={id}>
        <span className={`${styles.marker} ${step.done ? styles.markerDone : props.next ? styles.markerNext : ''}`} aria-hidden="true">
          {step.done ? <Icon name="check" size={15} /> : props.index}
        </span>
        <span>
          <span className={styles.stepTitle}>{stepTitles[step.key]}</span>
          <span className={styles.stepSummary}>{summary(step, guide)}</span>
        </span>
        <span className={styles.state}>{state}</span>
        <Icon name="down" size={16} className={styles.chevron} />
      </button>
      {props.open && (
        <div className={styles.stepBody} id={id}>
          <StepBody step={step} guide={guide} />
        </div>
      )}
    </li>
  )
}

function summary(step: SetupStep, guide: SetupGuide): string {
  const d = guide.details
  switch (step.key) {
    case 'storage':
      return step.done ? d.storageLocations.join(' · ') : guide.isOwner ? 'Where your media and downloads live' : 'Only the owner can add them'
    case 'rootFolders':
      return step.done
        ? `Movies: ${d.movieRootFolders === 0 ? 'none' : plural(d.movieRootFolders, 'folder', 'folders')} · Series: ${d.seriesRootFolders === 0 ? 'none' : plural(d.seriesRootFolders, 'folder', 'folders')}`
        : 'Where your movies and series are kept'
    case 'tmdb':
      return step.done ? 'Connected' : 'For titles, posters and episode lists'
    case 'language': {
      if (!step.done) return 'Which audio language to download'
      const language = d.releaseLanguage === 'any' || d.releaseLanguage === 'original' ? languageNames[d.releaseLanguage] : (d.releaseLanguageName ?? 'Custom')
      return `${language}${d.defaultProfile ? ` · default profile ${d.defaultProfile}` : ''}`
    }
    case 'indexers':
      return step.done ? plural(step.count, 'indexer', 'indexers') : 'Where StreamZH searches for releases'
    case 'downloads': {
      if (!step.done) return 'A Usenet server or a torrent client'
      const parts = []
      if (d.usenetServers > 0) parts.push(plural(d.usenetServers, 'Usenet server', 'Usenet servers'))
      if (d.builtInTorrent) parts.push('built-in torrent engine')
      if (d.torrentClients > 0) parts.push(plural(d.torrentClients, 'torrent client', 'torrent clients'))
      const text = parts.join(' · ')
      return text.charAt(0).toUpperCase() + text.slice(1)
    }
  }
}

function StepBody({ step, guide }: { step: SetupStep; guide: SetupGuide }) {
  const d = guide.details
  switch (step.key) {
    case 'storage':
      return (
        <>
          <p className={styles.why}>
            StreamZH only reads and writes folders inside its storage locations, so it never touches the rest of the computer. Add the folder or drive that holds
            your media and downloads, for example <code>{examplePath(guide, 'storage')}</code>
            {guide.isDocker ? ' (the path inside the container, where your volume is mounted)' : ''}.
          </p>
          {d.storageLocations.length > 0 && (
            <ul className={styles.list} aria-label="Storage locations">
              {d.storageLocations.map((l) => (
                <li key={l}>
                  <Icon name="hdd" size={13} />
                  <code>{l}</code>
                </li>
              ))}
            </ul>
          )}
          {guide.isOwner ? (
            <StorageForm guide={guide} />
          ) : (
            <p className={ui.infoBox}>Only the owner can add storage locations. Ask the owner to add the folder with your media and downloads.</p>
          )}
        </>
      )
    case 'rootFolders':
      return (
        <>
          <p className={styles.why}>
            Root folders hold your library: one or more for movies and for series. StreamZH scans them and moves finished downloads into them with clean names.
          </p>
          {step.done && d.seriesRootFolders === 0 && <p className={styles.hint}>Add a series folder too if you want series.</p>}
          {step.done && d.movieRootFolders === 0 && <p className={styles.hint}>Add a movie folder too if you want movies.</p>}
          <RootFolderForm guide={guide} />
          {step.done && (
            <div className={styles.actions}>
              <Link className={ui.ghost} to="/library/import">
                <Icon name="folderplus" size={15} />
                Import movies and series that are already there
              </Link>
            </div>
          )}
        </>
      )
    case 'tmdb':
      return (
        <>
          <p className={styles.why}>
            StreamZH looks up movies and series on TMDB (The Movie Database): titles, posters, release dates and episode lists. The key is free: sign up on
            themoviedb.org, open Settings → API and copy the <b>API Read Access Token</b>.
          </p>
          <div className={styles.actions}>
            <a className={ui.button} href={tmdbKeyUrl} target="_blank" rel="noreferrer">
              <Icon name="external" size={15} />
              Get a key on themoviedb.org
            </a>
          </div>
          {step.done && <p className={styles.hint}>TMDB is connected. Enter a new key here to replace it, or change it in Settings → Metadata.</p>}
          <TmdbForm />
        </>
      )
    case 'language':
      return (
        <>
          <p className={styles.why}>
            Which language should the releases StreamZH downloads be in? This picks the default quality profile for new movies and series (1080p, upgraded
            to Blu-ray quality). You can fine-tune profiles, add 4K and change this later in Settings → Profiles.
          </p>
          <LanguageChoice guide={guide} />
        </>
      )
    case 'indexers':
      return (
        <>
          <p className={styles.why}>
            Indexers are the sites StreamZH searches for releases: Newznab indexers for Usenet, Torznab for torrents (directly, or through Jackett or Prowlarr).
            Pick yours from the list of known indexers, or bring all of them over from Prowlarr.
          </p>
          <div className={styles.actions}>
            <Link className={step.done ? ui.button : ui.primary} to="/indexers?add=1">
              <Icon name="plus" size={15} />
              Add an indexer
            </Link>
            <Link className={ui.button} to="/indexers?import=prowlarr">
              <Icon name="download" size={15} />
              Import from Prowlarr
            </Link>
          </div>
        </>
      )
    case 'downloads':
      return (
        <>
          <p className={styles.why}>
            For Usenet, StreamZH downloads by itself: add your Usenet provider&apos;s server with your user name and password. For torrents, turn on the built-in
            torrent engine, or connect qBittorrent, Transmission or Deluge.
          </p>
          <DownloadActions guide={guide} />
        </>
      )
  }
}

function Extras({ guide }: { guide: SetupGuide }) {
  const d = guide.details
  const others = Math.max(0, d.users - 1)
  return (
    <>
      <div>
        <h2 className={styles.extrasHead}>When you are ready</h2>
        <p className={styles.extrasLead}>Optional: none of this is needed to start.</p>
      </div>
      <div className={styles.extras}>
        <Extra
          to="/settings/users"
          icon="users"
          title="Friends and requests"
          text="Invite friends with a link: they ask for movies and series, you approve them or let StreamZH do it."
          state={others > 0 ? plural(others, 'other account', 'other accounts') : d.invites > 0 ? plural(d.invites, 'invite link', 'invite links') : 'Only you so far'}
          done={others > 0}
        />
        <Extra
          to="/settings/discord"
          icon="chat"
          title="Discord bot"
          text="Let your Discord server request movies and series with slash commands, like Requestrr."
          state={d.discordEnabled ? 'On' : 'Off'}
          done={d.discordEnabled}
        />
        <Extra
          to="/settings/connect"
          icon="plug"
          title="Plex, Jellyfin or Emby"
          text="Update your media server's library after every import, and send notifications."
          state={d.connections > 0 ? plural(d.connections, 'connection', 'connections') : 'Not connected'}
          done={d.connections > 0}
        />
        <Extra
          to="/library/import"
          icon="folderplus"
          title="Import your library"
          text="Already have movies and series in your root folders? Add them to StreamZH in one go."
          state={d.movieRootFolders + d.seriesRootFolders > 0 ? 'Root folders are ready' : 'Add a root folder first'}
          done={false}
        />
      </div>
    </>
  )
}

function Extra(props: { to: string; icon: IconName; title: string; text: string; state: ReactNode; done: boolean }) {
  return (
    <Link className={styles.extra} to={props.to}>
      <span className={styles.extraIcon} aria-hidden="true">
        <Icon name={props.icon} size={18} />
      </span>
      <h3>{props.title}</h3>
      <p>{props.text}</p>
      <span className={`${styles.extraState} ${props.done ? styles.extraDone : ''}`}>
        {props.done && <Icon name="check" size={13} />} {props.state}
      </span>
    </Link>
  )
}
