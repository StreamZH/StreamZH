import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useId, useMemo, useState } from 'react'
import { discordApi, discordKeys, maskedToken, type DiscordSettings, type DiscordStatus, type DiscordTestResult, type SeriesSeasonsMode } from '../api/discord'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import { relativeTime } from '../lib/format'
import styles from './Discord.module.css'
import { IdPicker, type IdOption } from './IdPicker'
import RequestrrImportDialog from './RequestrrImportDialog'
import { Row, SaveBar, Section, Select, SettingsPage, Toggle } from './SettingsKit'
import { useSettingsForm } from './useSettingsForm'

const seasonModes: { value: SeriesSeasonsMode; label: string }[] = [
  { value: 'choose', label: 'Choose seasons (all or several)' },
  { value: 'single', label: 'One season per request' },
  { value: 'all', label: 'Whole series only' },
]

/** The bot's state in one line, with a dot like the status window. */
function StatusLine({ status, enabled }: { status: DiscordStatus; enabled: boolean }) {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    if (!status.nextRetry) return
    const timer = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(timer)
  }, [status.nextRetry])

  const retryIn = status.nextRetry ? Math.max(0, Math.ceil((new Date(status.nextRetry).getTime() - now) / 1000)) : null
  const [dot, title, detail] = (() => {
    switch (status.state) {
      case 'connected':
        return [ui.dotOk, `Connected as ${status.botName ?? 'bot'}`, `${status.guilds} server${status.guilds === 1 ? '' : 's'}${status.since ? ` · since ${relativeTime(status.since)}` : ''}`]
      case 'connecting':
        return [ui.dotBusy, 'Connecting…', null]
      case 'reconnecting':
        return [ui.dotWarn, retryIn ? `Reconnecting in ${retryIn} s` : 'Reconnecting…', status.error]
      case 'error':
        return [ui.dotErr, 'Stopped', status.error]
      default:
        return [styles.dotOff, enabled ? 'Off: save a bot token to start' : 'Off', null]
    }
  })()

  return (
    <div className={styles.status} role="status">
      <span className={dot} />
      <span className={styles.statusTitle}>{title}</span>
      {detail && <span className={styles.statusDetail}>{detail}</span>}
    </div>
  )
}

/** Settings → Discord: the built-in request bot (replaces Requestrr). */
export default function DiscordPage() {
  const id = useId()
  const queryClient = useQueryClient()
  const [importOpen, setImportOpen] = useState(false)
  const [test, setTest] = useState<DiscordTestResult | null>(null)
  const [testShown, setTestShown] = useState(false)

  const form = useSettingsForm<DiscordSettings>({
    queryKey: discordKeys.settings,
    load: discordApi.get,
    save: (draft) => discordApi.save(draft),
  })
  const v = form.values

  // Live state of the bot while the page is open.
  const status = useQuery({ queryKey: discordKeys.status, queryFn: discordApi.status, refetchInterval: 4000, enabled: !!v })
  const liveStatus = status.data ?? v?.status

  const testMutation = useMutation({
    mutationFn: (token?: string) => discordApi.test(token),
    onSuccess: setTest,
  })

  // With a saved token, look up the servers, channels and roles once, so the pickers show names.
  const hasToken = form.query.data?.hasToken ?? false
  const { mutate: runTest } = testMutation
  useEffect(() => {
    if (hasToken) runTest(undefined)
  }, [hasToken, runTest])

  const guilds = useMemo(() => (test?.ok ? test.guilds : []), [test])
  const guildOptions: IdOption[] = guilds.map((g) => ({ id: g.id, label: g.name }))
  const multiGuild = guilds.length > 1
  const channelOptions: IdOption[] = guilds
    .filter((g) => !v || v.guildIds.length === 0 || v.guildIds.includes(g.id))
    .flatMap((g) => g.channels.map((c) => ({ id: c.id, label: c.name, group: multiGuild ? g.name : undefined })))
  const roleOptions: IdOption[] = guilds
    .filter((g) => !v || v.guildIds.length === 0 || v.guildIds.includes(g.id))
    .flatMap((g) => g.roles.map((r) => ({ id: r.id, label: r.name, group: multiGuild ? g.name : undefined })))

  const newToken = v && v.botToken !== maskedToken && v.botToken.trim() !== '' ? v.botToken.trim() : undefined
  const inviteUrl = v?.inviteUrl ?? test?.inviteUrl ?? null

  return (
    <SettingsPage
      title="Discord"
      lead="A Discord bot for requests: friends search and request movies and series with /movie and /tv, and hear back when they are ready. Replaces Requestrr."
      actions={
        <button type="button" className={`${ui.button} ${ui.sm}`} onClick={() => setImportOpen(true)}>
          <Icon name="download" size={14} />
          Import from Requestrr
        </button>
      }
    >
      {form.query.isError && <p className={ui.errBox}>{form.query.error.message}</p>}
      {v && liveStatus && (
        <>
          <section className={`${ui.panel} ${styles.statusPanel}`} aria-label="Bot status">
            <StatusLine status={liveStatus} enabled={v.enabled} />
            {liveStatus.permissionProblem && (
              <p className={styles.problem}>
                <Icon name="alert" size={15} />
                <span>{liveStatus.permissionProblem}</span>
              </p>
            )}
            {!v.requestsReady && (
              <p className={styles.problem}>
                <Icon name="info" size={15} />
                <span>Requests are not available in this build yet: the bot answers every request with a notice.</span>
              </p>
            )}
          </section>

          <Section id="bot" title="Bot" hiddenCount={1} description="Create the bot once in Discord’s Developer Portal (steps below), paste its token, then add it to your server.">
            <Row label="Discord bot" help="Runs inside StreamZH while this is on and a token is saved.">
              <Toggle checked={v.enabled} onChange={(enabled) => form.set({ enabled })} on="On" off="Off" />
            </Row>

            <Row
              label="Bot token"
              htmlFor={`${id}-token`}
              error={form.error('botToken')}
              help={v.hasToken && v.botToken === maskedToken ? 'Saved. Type a new token to replace it.' : 'Developer Portal → your application → Bot → Reset Token.'}
            >
              <input
                id={`${id}-token`}
                className={`${ui.input} ${ui.mono} ${styles.token}`}
                type="password"
                autoComplete="off"
                spellCheck={false}
                placeholder={v.hasToken ? 'Saved' : 'Paste the bot token'}
                value={v.botToken}
                onFocus={(e) => v.botToken === maskedToken && e.target.select()}
                onChange={(e) => form.set({ botToken: e.target.value })}
              />
              <div className={styles.actions}>
                {v.hasToken && v.botToken === maskedToken ? (
                  <span className={`${ui.pill} ${ui.pillOk}`}>Saved</span>
                ) : v.hasToken && v.botToken === '' ? (
                  <span className={`${ui.pill} ${ui.pillWarn}`}>Removed on save</span>
                ) : !v.hasToken && !newToken ? (
                  <span className={`${ui.pill} ${ui.pillErr}`}>Not set</span>
                ) : null}
                <button
                  type="button"
                  className={`${ui.button} ${ui.sm}`}
                  disabled={testMutation.isPending || (!newToken && !v.hasToken)}
                  onClick={() => {
                    setTestShown(true)
                    testMutation.mutate(newToken)
                  }}
                >
                  <Icon name="plug" size={14} />
                  {testMutation.isPending && testShown ? 'Testing…' : 'Test'}
                </button>
                {v.hasToken && v.botToken === maskedToken && (
                  <button type="button" className={`${ui.ghost} ${ui.sm}`} onClick={() => form.set({ botToken: '', enabled: false })}>
                    <Icon name="trash" size={14} />
                    Remove
                  </button>
                )}
              </div>
              {testShown && (
                <p aria-live="polite" className={styles.testResult}>
                  {test?.ok && (
                    <span className={ui.ok}>
                      Works: {test.botName}, in {test.guilds.length} server{test.guilds.length === 1 ? '' : 's'}
                      {test.guilds.length > 0 ? ` (${test.guilds.map((g) => g.name).join(', ')})` : ''}
                    </span>
                  )}
                  {test && !test.ok && <span className={ui.error}>{test.error}</span>}
                  {testMutation.error && <span className={ui.error}>{testMutation.error.message}</span>}
                </p>
              )}
            </Row>

            <Row
              label="Add to a server"
              help={inviteUrl ? 'Opens Discord to add the bot with the permissions it needs: view channels, send messages, embed links, slash commands.' : 'Save the token first: the invite link needs the application id.'}
            >
              <div className={styles.actions}>
                {inviteUrl ? (
                  <>
                    <a className={`${ui.primary} ${ui.sm}`} href={inviteUrl} target="_blank" rel="noreferrer noopener">
                      <Icon name="external" size={14} />
                      Invite the bot
                    </a>
                    <button type="button" className={`${ui.ghost} ${ui.sm}`} onClick={() => void navigator.clipboard?.writeText(inviteUrl)}>
                      <Icon name="copy" size={14} />
                      Copy link
                    </button>
                  </>
                ) : (
                  <span className={ui.muted}>No application id yet</span>
                )}
              </div>
            </Row>

            <Row label="Application id" htmlFor={`${id}-app`} error={form.error('applicationId')} help="General Information in the Developer Portal. Filled in when the bot connects." advanced>
              <input
                id={`${id}-app`}
                className={`${ui.input} ${ui.mono} ${styles.idInput}`}
                inputMode="numeric"
                spellCheck={false}
                value={v.applicationId ?? ''}
                onChange={(e) => form.set({ applicationId: e.target.value.trim() || null })}
              />
            </Row>

            <Row
              label="Servers"
              error={form.error('guildIds')}
              help="Where the commands are added (they show up at once). Empty: every server the bot is in."
              wide
            >
              <IdPicker id={`${id}-guilds`} value={v.guildIds} onChange={(guildIds) => form.set({ guildIds })} options={guildOptions} emptyText="Every server" addLabel="Add a server" />
            </Row>

            <Row label="Request channels" error={form.error('channelIds')} help="Channels where /movie and /tv work. Empty: every channel." wide>
              <IdPicker
                id={`${id}-channels`}
                value={v.channelIds}
                onChange={(channelIds) => form.set({ channelIds })}
                options={channelOptions}
                emptyText="Every channel"
                addLabel="Add a channel"
                prefix="#"
              />
            </Row>
          </Section>

          <Section
            id="requesters"
            title="Requesters and notifications"
            description="People who linked their Discord account in their StreamZH profile request as themselves, with their own limits and approval rules. Everybody hears back when a request is approved, declined or available."
            hiddenCount={5}
          >
            <Row label="Unlinked Discord users" help="Members without a StreamZH account may request too. Their requests always wait for an admin." advanced error={form.error('allowGuests')}>
              <Toggle checked={v.allowGuests} onChange={(allowGuests) => form.set({ allowGuests })} on="Allowed" off="Only linked accounts" />
            </Row>
            <Row label="Required role" help="Unlinked users need one of these roles. Empty: every member." advanced wide error={form.error('guestRoleIds')}>
              <IdPicker
                id={`${id}-roles`}
                value={v.guestRoleIds}
                onChange={(guestRoleIds) => form.set({ guestRoleIds })}
                options={roleOptions}
                emptyText="Any member"
                addLabel="Add a role"
                prefix="@"
              />
            </Row>
            <Row label="Direct messages" help="The request commands also work in a private chat with the bot (only when no servers are listed above)." advanced>
              <Toggle checked={v.allowDirectMessages} onChange={(allowDirectMessages) => form.set({ allowDirectMessages })} />
            </Row>
            <Row label="Message the requester" help="A direct message from the bot to the person who asked." advanced>
              <Toggle checked={v.dmRequesters} onChange={(dmRequesters) => form.set({ dmRequesters })} />
            </Row>
            <Row label="Notify channel" help="Also post to this channel (and mention the requester). Empty: no channel." advanced wide error={form.error('notifyChannelId')}>
              <IdPicker
                id={`${id}-notify`}
                value={v.notifyChannelId ? [v.notifyChannelId] : []}
                onChange={(ids) => form.set({ notifyChannelId: ids[0] ?? null })}
                options={channelOptions}
                emptyText="No channel"
                addLabel="Choose a channel"
                prefix="#"
                single
              />
            </Row>
          </Section>

          <Section id="commands" title="Commands" description="Names and rules of the slash commands. /requests, /link and /help are always there." advanced>
            <Row label="Movie command" htmlFor={`${id}-movie`} error={form.error('movieCommand')} help="Without the slash. Empty switches movie requests off.">
              <input id={`${id}-movie`} className={`${ui.input} ${styles.idInput}`} spellCheck={false} value={v.movieCommand} onChange={(e) => form.set({ movieCommand: e.target.value })} />
            </Row>
            <Row label="Series command" htmlFor={`${id}-tv`} error={form.error('tvCommand')} help="Empty switches series requests off.">
              <input id={`${id}-tv`} className={`${ui.input} ${styles.idInput}`} spellCheck={false} value={v.tvCommand} onChange={(e) => form.set({ tvCommand: e.target.value })} />
            </Row>
            <Row label="Series seasons" htmlFor={`${id}-seasons`} error={form.error('seriesSeasons')}>
              <Select id={`${id}-seasons`} value={v.seriesSeasons} onChange={(seriesSeasons) => form.set({ seriesSeasons })} options={seasonModes} />
            </Row>
            <Row label="Status text" htmlFor={`${id}-statusText`} error={form.error('statusText')} help="Shown under the bot’s name in the member list.">
              <input id={`${id}-statusText`} className={`${ui.input} ${styles.idInput}`} maxLength={128} value={v.statusText} onChange={(e) => form.set({ statusText: e.target.value })} />
            </Row>
          </Section>

          <section className={`${ui.panel} ${styles.guide}`} aria-labelledby={`${id}-guide`}>
            <h2 id={`${id}-guide`} className={styles.guideTitle}>
              Create the bot
            </h2>
            <ol className={styles.steps}>
              <li>
                Open the{' '}
                <a href="https://discord.com/developers/applications" target="_blank" rel="noreferrer noopener">
                  Discord Developer Portal
                </a>{' '}
                and create a <em>New Application</em> (any name, e.g. “StreamZH”).
              </li>
              <li>
                Under <em>Bot</em>, choose <em>Reset Token</em> and paste the token above. No privileged intents are needed.
              </li>
              <li>Switch the bot on, save, then use <em>Invite the bot</em> to add it to your server.</li>
              <li>
                Friends link their account: StreamZH profile → Discord → <em>Create code</em>, then <code>/link CODE</code> in Discord.
              </li>
            </ol>
          </section>

          <SaveBar dirty={form.dirty} saving={form.saving} saved={form.saved} error={form.formError} onSave={form.save} onReset={form.reset} />
        </>
      )}
      {importOpen && (
        <RequestrrImportDialog
          names={Object.fromEntries([...channelOptions.map((o) => [o.id, `#${o.label}`]), ...roleOptions.map((o) => [o.id, `@${o.label}`])])}
          onClose={() => {
            setImportOpen(false)
            void queryClient.invalidateQueries({ queryKey: discordKeys.status })
          }}
        />
      )}
    </SettingsPage>
  )
}
