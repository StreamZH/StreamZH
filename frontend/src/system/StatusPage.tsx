import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { generalApi, generalKeys, type DiskSpaceEntry, type SystemInfo } from '../api/general'
import ui from '../components/ui.module.css'
import { formatBytes, formatDateTime, formatDuration } from '../lib/format'
import { Section } from '../settings/SettingsKit'
import { RestartControls } from './Restart'
import styles from './System.module.css'
import { SystemPage } from './SystemPage'

const modes: Record<SystemInfo['runtimeMode'], string> = {
  docker: 'Docker container',
  windowsService: 'Windows service',
  systemd: 'systemd service',
  console: 'Program (console)',
}

/** System → Status (like Radarr): health, about, disk space, licence. */
export default function StatusPage() {
  const info = useQuery({ queryKey: generalKeys.info, queryFn: generalApi.info, refetchInterval: 30_000 })
  const data = info.data

  return (
    <SystemPage title="Status" lead="Version, health, disk space and runtime." actions={<RestartControls />}>
      {info.isError && <p className={ui.error}>{info.error.message}</p>}
      {data && (
        <>
          <Section id="health" title="Health" description="The same notices as the status window, in one list.">
            {data.health.length === 0 ? (
              <p className={styles.ok}>No problems found.</p>
            ) : (
              <ul className={styles.health}>
                {data.health.map((notice) => (
                  <li key={notice.key} className={styles.notice}>
                    <span className={`${styles.dot} ${notice.level === 'error' ? styles.dotError : notice.level === 'warn' ? styles.dotWarn : ''}`} />
                    <span className={styles.noticeText}>
                      <span>{notice.title}</span>
                      {notice.sub && <span className={styles.noticeSub}>{notice.sub}</span>}
                    </span>
                    {notice.link && (
                      <Link className={styles.noticeLink} to={notice.link}>
                        Open →
                      </Link>
                    )}
                  </li>
                ))}
              </ul>
            )}
          </Section>

          <Section id="about" title="About">
            <dl className={styles.facts}>
              <dt>Version</dt>
              <dd>
                {data.appName} {data.version}
              </dd>
              <dt>.NET</dt>
              <dd>{data.runtime}</dd>
              <dt>Operating system</dt>
              <dd>
                {data.operatingSystem} ({data.architecture})
              </dd>
              <dt>Runs as</dt>
              <dd>{modes[data.runtimeMode]}</dd>
              <dt>Listening on</dt>
              <dd className={styles.mono}>
                {data.listening.join(', ')}
                {data.urlBase && ` · URL base ${data.urlBase}`}
                {data.hostOverride && <span className={styles.muted}> (from the command line)</span>}
              </dd>
              <dt>Data folder</dt>
              <dd className={styles.mono}>{data.dataDirectory}</dd>
              <dt>Database</dt>
              <dd>
                <span className={styles.mono}>{data.databaseFile}</span>
                <span className={styles.muted}>
                  {' '}
                  · {formatBytes(data.databaseSize)} · schema {data.databaseSchemaVersion}
                </span>
              </dd>
              <dt>Started</dt>
              <dd>
                {formatDateTime(data.startedAt)} <span className={styles.muted}>· up {formatDuration(data.uptimeSeconds)}</span>
              </dd>
              {data.restore && (
                <>
                  <dt>Restore at start</dt>
                  <dd className={data.restore.success ? undefined : styles.errorText}>{data.restore.message}</dd>
                </>
              )}
            </dl>
          </Section>

          <Section id="disk-space" title="Disk space">
            <div className={`${styles.table} ${styles.disk}`} role="table" aria-label="Disk space">
              <div className={styles.head} role="row">
                <span role="columnheader">Location</span>
                <span role="columnheader">Folder</span>
                <span role="columnheader" className={styles.right}>
                  Free
                </span>
                <span role="columnheader" className={styles.right}>
                  Total
                </span>
                <span role="columnheader">Used</span>
              </div>
              {data.diskSpace.map((d) => (
                <DiskRow key={d.path} entry={d} />
              ))}
            </div>
          </Section>

          <Section id="more" title="More info">
            <dl className={styles.facts}>
              <dt>Licence</dt>
              <dd>GNU General Public License v3.0 only ({data.license})</dd>
              <dt>Based on</dt>
              <dd>Parts of Radarr, Sonarr and Prowlarr (GPL-3.0); details in NOTICE.md next to the program.</dd>
              <dt>Privacy</dt>
              <dd>StreamZH sends no usage data or analytics.</dd>
            </dl>
          </Section>
        </>
      )}
    </SystemPage>
  )
}

function DiskRow({ entry: d }: { entry: DiskSpaceEntry }) {
  const used = d.totalBytes && d.freeBytes != null ? 1 - d.freeBytes / d.totalBytes : null
  const low = d.freeBytes != null && d.freeBytes < 10 * 1024 ** 3
  return (
    <div className={styles.row} role="row">
      <span role="cell" className={`${styles.cell} ${styles.name}`}>
        {d.label}
      </span>
      <span role="cell" className={`${styles.cell} ${styles.mono}`} title={d.drive ?? undefined}>
        {d.path}
      </span>
      <span role="cell" className={`${styles.cell} ${styles.right} ${low ? styles.warnText : ''}`}>
        <span className={styles.cellLabel}>Free</span>
        {d.freeBytes != null ? formatBytes(d.freeBytes) : '–'}
      </span>
      <span role="cell" className={`${styles.cell} ${styles.right}`}>
        <span className={styles.cellLabel}>Total</span>
        {d.totalBytes != null ? formatBytes(d.totalBytes) : '–'}
      </span>
      <span role="cell" className={styles.cell}>
        {d.problem ? (
          <span className={styles.errorText}>{d.problem}</span>
        ) : used != null ? (
          <div className={styles.bar} title={`${Math.round(used * 100)} % used`}>
            <div className={`${styles.barFill} ${used > 0.95 ? styles.barError : used > 0.85 ? styles.barWarn : ''}`} style={{ width: `${Math.round(used * 100)}%` }} />
          </div>
        ) : null}
      </span>
    </div>
  )
}
