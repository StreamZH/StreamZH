import { useMutation, useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { ApiError } from '../api/client'
import { builtInImplementation, clientAddress, downloadClientKeys, downloadClientsApi, type DownloadClient } from '../api/downloadClients'
import { downloadSettingsApi, downloadSettingsKeys } from '../api/downloadSettings'
import { usenetApi, usenetKeys } from '../api/usenet'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import { formatAgo } from '../lib/format'
import system from '../system/System.module.css'
import { ClientMark } from './ClientMark'
import styles from './ClientsStatus.module.css'

/** System → Status: every download client with its state, and a Test button per torrent client (0.11). */
export function ClientsStatusPanel({ canTest }: { canTest: boolean }) {
  const clients = useQuery({ queryKey: downloadClientKeys.list, queryFn: downloadClientsApi.list, refetchInterval: 30_000, retry: false })
  const settings = useQuery({ queryKey: downloadSettingsKeys.settings, queryFn: downloadSettingsApi.get, enabled: canTest })
  const servers = useQuery({ queryKey: usenetKeys.servers, queryFn: usenetApi.list, enabled: canTest })
  // Builds without torrent support answer 404: the panel then only shows Usenet.
  const unsupported = clients.error instanceof ApiError && clients.error.status === 404
  const torrent = (clients.data ?? []).filter((c) => c.protocol === 'torrent').sort((a, b) => Number(b.enable) - Number(a.enable) || a.priority - b.priority)
  const blackhole = settings.data?.client === 'blackhole'
  const usenetServers = (servers.data ?? []).filter((s) => s.enabled).length
  const usenetWarn = !blackhole && servers.isSuccess && usenetServers === 0

  return (
    <section id="download-clients" className={ui.panel}>
      <div className={system.phead}>
        <Icon name="download" size={17} className={system.panelIcon} />
        <h2>Download clients</h2>
        <Link className={`${system.pheadAside} ${styles.asideLink}`} to="/settings/downloadclients">
          Settings
          <Icon name="right" size={13} />
        </Link>
      </div>
      <ul className={styles.list}>
        <li className={styles.row}>
          <span className={usenetWarn ? ui.dotWarn : ui.dotOk} aria-hidden="true" />
          <ClientMark implementation={blackhole ? 'usenet' : 'streamzh'} size={28} />
          <span className={styles.text}>
            <b>{blackhole ? 'Blackhole folder' : 'Usenet engine'}</b>
            <small>
              {blackhole
                ? 'NZB files for another downloader'
                : servers.isSuccess
                  ? usenetWarn
                    ? 'No Usenet server: Usenet downloads cannot start'
                    : `Built in · ${usenetServers} server${usenetServers === 1 ? '' : 's'}`
                  : 'Built in'}
            </small>
          </span>
          <span className={styles.kind}>Usenet</span>
        </li>
        {torrent.map((c) => (
          <ClientRow key={c.id} client={c} canTest={canTest} />
        ))}
      </ul>
      {clients.isSuccess && torrent.length === 0 && (
        <p className={styles.none}>
          No torrent client. Torznab indexers need one: <Link to="/settings/downloadclients">add a client or turn on the built-in engine</Link>.
        </p>
      )}
      {clients.isError && !unsupported && <p className={ui.error}>{clients.error.message}</p>}
    </section>
  )
}

function ClientRow({ client: c, canTest }: { client: DownloadClient; canTest: boolean }) {
  const test = useMutation({ mutationFn: () => downloadClientsApi.test(c) })
  const failing = c.enable && c.status != null && !c.status.healthy
  const tested = test.isSuccess ? test.data.ok : test.isError ? false : null
  const bad = tested === false || (tested == null && failing)
  const dot = !c.enable ? styles.dotOff : bad ? ui.dotErr : ui.dotOk
  const address = c.implementation === builtInImplementation ? 'Inside StreamZH' : clientAddress(c)
  const message = test.isPending
    ? 'Testing…'
    : test.isSuccess
      ? test.data.message
      : test.isError
        ? test.error.message
        : !c.enable
          ? 'Disabled'
          : failing
            ? (c.status?.message ?? 'Unreachable')
            : c.status?.checkedAt
              ? `Checked ${formatAgo(c.status.checkedAt)}`
              : null

  return (
    <li className={styles.row}>
      <span className={dot} aria-label={!c.enable ? 'Disabled' : bad ? 'Failing' : 'OK'} role="img" />
      <ClientMark implementation={c.implementation} size={28} />
      <span className={styles.text}>
        <b>
          {c.name}
          <span className={styles.impl}>
            {c.implementationName}
            {c.status?.version ? ` ${c.status.version}` : ''}
          </span>
        </b>
        <small className={bad ? styles.errText : tested ? styles.okText : undefined}>
          {[address, message].filter(Boolean).join(' · ')}
        </small>
      </span>
      {canTest && c.enable ? (
        <button type="button" className={`${ui.button} ${ui.sm}`} onClick={() => test.mutate()} disabled={test.isPending}>
          {test.isPending ? 'Testing…' : 'Test'}
        </button>
      ) : (
        <span className={styles.kind}>Torrent</span>
      )}
    </li>
  )
}
