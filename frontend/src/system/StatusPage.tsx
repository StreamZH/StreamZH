import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { generalApi, generalKeys, type DiskSpaceEntry, type SystemInfo } from '../api/general'
import Icon, { type IconName } from '../components/Icon'
import { Skeleton } from '../components/Loader'
import ui from '../components/ui.module.css'
import { formatBytes, formatDateTime, formatDuration } from '../lib/format'
import { RestartControls } from './Restart'
import styles from './System.module.css'
import { SystemPage } from './SystemPage'

const modes: Record<SystemInfo['runtimeMode'], string> = {
  docker: 'Docker container',
  windowsService: 'Windows service',
  systemd: 'systemd service',
  console: 'Program (console)',
}

const modeIcons: Record<SystemInfo['runtimeMode'], IconName> = {
  docker: 'box',
  windowsService: 'server',
  systemd: 'server',
  console: 'terminal',
}

/** System → Status (like CloudZH's System Health): metrics on top, health checks, disk space, about, licence. */
export default function StatusPage() {
  const info = useQuery({ queryKey: generalKeys.info, queryFn: generalApi.info, refetchInterval: 30_000 })
  const data = info.data
  const errors = data?.health.filter((n) => n.level === 'error').length ?? 0
  const warnings = data?.health.filter((n) => n.level === 'warn').length ?? 0

  return (
    <SystemPage title="Status" lead="Version, health, disk space and runtime." actions={<RestartControls />}>
      {info.isError && <p className={ui.error}>{info.error.message}</p>}
      {info.isPending && (
        <div className={`${ui.panel} ${styles.loading}`}>
          <Skeleton rows={5} />
        </div>
      )}
      {data && (
        <>
          <div className={styles.metrics}>
            <div className={`${ui.panel} ${styles.metric}`}>
              <span className={styles.metricTop}>
                <span className={ui.eyebrow}>Version</span>
                <Icon name="sparkle" size={16} className={styles.metricIcon} />
              </span>
              <span className={styles.metricValue}>{data.version}</span>
              <span className={styles.metricSub}>
                {data.appName} · {data.runtime}
              </span>
            </div>
            <div className={`${ui.panel} ${styles.metric}`}>
              <span className={styles.metricTop}>
                <span className={ui.eyebrow}>Uptime</span>
                <Icon name="clock" size={16} className={styles.metricIcon} />
              </span>
              <span className={styles.metricValue}>{formatDuration(data.uptimeSeconds)}</span>
              <span className={styles.metricSub} title={formatDateTime(data.startedAt)}>
                Since {formatDateTime(data.startedAt)}
              </span>
            </div>
            <div className={`${ui.panel} ${styles.metric}`}>
              <span className={styles.metricTop}>
                <span className={ui.eyebrow}>Runs as</span>
                <Icon name={modeIcons[data.runtimeMode]} size={16} className={styles.metricIcon} />
              </span>
              <span className={`${styles.metricValue} ${styles.metricText}`}>{modes[data.runtimeMode]}</span>
              <span className={styles.metricSub}>
                {data.operatingSystem} ({data.architecture})
              </span>
            </div>
            <div className={`${ui.panel} ${styles.metric}`}>
              <span className={styles.metricTop}>
                <span className={ui.eyebrow}>Health</span>
                <Icon name="heart" size={16} className={styles.metricIcon} />
              </span>
              <span className={`${styles.metricValue} ${errors ? styles.errorText : warnings ? styles.warnText : styles.ok}`}>
                {errors + warnings === 0 ? 'All good' : errors + warnings}
                {errors + warnings > 0 && <small>{errors ? (errors === 1 ? 'error' : 'errors') : warnings === 1 ? 'warning' : 'warnings'}</small>}
              </span>
              <span className={styles.metricSub}>
                {errors && warnings ? `and ${warnings} warning${warnings === 1 ? '' : 's'}` : errors + warnings === 0 ? 'No problems found' : 'See the checks below'}
              </span>
            </div>
          </div>

          <div className={styles.split}>
            <section id="health" className={ui.panel}>
              <div className={styles.phead}>
                <Icon name="heart" size={17} className={styles.panelIcon} />
                <h2>Health</h2>
                <span className={styles.pheadAside}>Same as the status window</span>
              </div>
              {data.health.length === 0 ? (
                <div className={styles.allGood}>
                  <Icon name="check" size={16} />
                  <span>
                    <b>No problems found.</b> Indexers, download servers, folders and tools all respond.
                  </span>
                </div>
              ) : (
                <ul className={styles.health}>
                  {data.health.map((notice) => (
                    <li
                      key={notice.key}
                      className={`${styles.check} ${notice.level === 'error' ? styles.checkErr : notice.level === 'warn' ? styles.checkWarn : styles.checkInfo}`}
                    >
                      <span className={styles.checkIcon} aria-hidden="true">
                        <Icon name={notice.level === 'error' ? 'x' : notice.level === 'warn' ? 'alert' : 'info'} size={15} />
                      </span>
                      <span className={styles.noticeText}>
                        <b>{notice.title}</b>
                        {notice.sub && <small>{notice.sub}</small>}
                      </span>
                      {notice.link && (
                        <Link className={`${ui.ghost} ${ui.sm}`} to={notice.link}>
                          Open
                          <Icon name="right" size={14} />
                        </Link>
                      )}
                    </li>
                  ))}
                </ul>
              )}
            </section>

            <section id="disk-space" className={ui.panel}>
              <div className={styles.phead}>
                <Icon name="hdd" size={17} className={styles.panelIcon} />
                <h2>Disk space</h2>
              </div>
              <ul className={styles.disks} aria-label="Disk space">
                {data.diskSpace.map((d) => (
                  <DiskRow key={d.path} entry={d} />
                ))}
              </ul>
            </section>
          </div>

          <section id="about" className={ui.panel}>
            <div className={styles.phead}>
              <Icon name="info" size={17} className={styles.panelIcon} />
              <h2>About</h2>
            </div>
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
          </section>

          <section id="more" className={ui.panel}>
            <div className={styles.phead}>
              <Icon name="filetext" size={17} className={styles.panelIcon} />
              <h2>More info</h2>
            </div>
            <dl className={styles.facts}>
              <dt>Licence</dt>
              <dd>GNU General Public License v3.0 only ({data.license})</dd>
              <dt>Based on</dt>
              <dd>Parts of Radarr, Sonarr and Prowlarr (GPL-3.0); details in NOTICE.md next to the program.</dd>
              <dt>Privacy</dt>
              <dd>StreamZH sends no usage data or analytics.</dd>
            </dl>
          </section>
        </>
      )}
    </SystemPage>
  )
}

function DiskRow({ entry: d }: { entry: DiskSpaceEntry }) {
  const used = d.totalBytes && d.freeBytes != null ? 1 - d.freeBytes / d.totalBytes : null
  const low = d.freeBytes != null && d.freeBytes < 10 * 1024 ** 3
  const percent = used != null ? Math.round(used * 100) : null
  return (
    <li className={styles.disk}>
      <div className={styles.diskTop}>
        <span className={styles.name}>{d.label}</span>
        <span className={`${styles.diskFree} ${low ? styles.warnText : ''}`}>
          {d.freeBytes != null ? `${formatBytes(d.freeBytes)} free` : '–'}
          {d.totalBytes != null && <span className={styles.muted}> of {formatBytes(d.totalBytes)}</span>}
        </span>
      </div>
      {d.problem ? (
        <span className={styles.errorText}>{d.problem}</span>
      ) : (
        percent != null && (
          <div className={styles.bar} role="meter" aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent} aria-label={`${d.label}: ${percent} % used`}>
            <div
              className={`${styles.barFill} ${used! > 0.95 ? styles.barError : used! > 0.85 ? styles.barWarn : ''}`}
              style={{ width: `${percent}%` }}
            />
          </div>
        )
      )}
      <div className={styles.diskBottom}>
        <span className={styles.diskPath} title={d.drive ?? undefined}>
          {d.path}
        </span>
        {percent != null && <span className={styles.muted}>{percent} % used</span>}
      </div>
    </li>
  )
}
