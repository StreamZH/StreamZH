import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { ApiError } from '../api/client'
import {
  days,
  downloadSettingsApi,
  downloadSettingsKeys,
  type Day,
  type DownloadSchedule,
  type DownloadScheduleInput,
  type ScheduleAction,
} from '../api/downloadSettings'
import { usenetApi, usenetKeys } from '../api/usenet'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import styles from './DownloadsPage.module.css'
import { Section, Toggle } from './SettingsKit'

const actions: { value: ScheduleAction; label: string }[] = [
  { value: 'pause', label: 'Pause downloads' },
  { value: 'resume', label: 'Resume downloads' },
  { value: 'speedLimit', label: 'Set speed limit' },
  { value: 'pauseServer', label: 'Pause a server' },
  { value: 'resumeServer', label: 'Resume a server' },
  { value: 'pauseCategory', label: 'Pause a category' },
  { value: 'resumeCategory', label: 'Resume a category' },
]

const everyDay: Day[] = days.map((d) => d.value)

const empty: DownloadScheduleInput = { enabled: true, time: '23:00', days: everyDay, action: 'pause', argument: '' }

/** SABnzbd's Scheduling page: "at HH:MM on these days do this". */
export function DownloadScheduleSection() {
  const queryClient = useQueryClient()
  const rules = useQuery({ queryKey: downloadSettingsKeys.schedules, queryFn: downloadSettingsApi.schedules })
  const servers = useQuery({ queryKey: usenetKeys.servers, queryFn: usenetApi.list })
  const categories = useQuery({ queryKey: downloadSettingsKeys.categories, queryFn: downloadSettingsApi.categories })
  const [editing, setEditing] = useState<number | 'new' | null>(null)
  const [draft, setDraft] = useState<DownloadScheduleInput>(empty)

  const refresh = () => queryClient.invalidateQueries({ queryKey: downloadSettingsKeys.schedules })
  const save = useMutation({
    mutationFn: () => (editing === 'new' ? downloadSettingsApi.addSchedule(draft) : downloadSettingsApi.saveSchedule(editing as number, draft)),
    onSuccess: async () => {
      setEditing(null)
      await refresh()
    },
  })
  const toggle = useMutation({
    mutationFn: (rule: DownloadSchedule) => downloadSettingsApi.saveSchedule(rule.id, { ...rule, enabled: !rule.enabled }),
    onSuccess: refresh,
  })
  const remove = useMutation({ mutationFn: downloadSettingsApi.deleteSchedule, onSuccess: refresh })

  const open = (rule: DownloadSchedule | null) => {
    save.reset()
    setDraft(rule ? { enabled: rule.enabled, time: rule.time, days: rule.days, action: rule.action, argument: rule.argument } : empty)
    setEditing(rule ? rule.id : 'new')
  }

  const fieldErrors = save.error instanceof ApiError ? save.error.fieldErrors : {}
  const formError = save.error && Object.keys(fieldErrors).length === 0 ? save.error.message : null

  const setAction = (action: ScheduleAction) => {
    const argument =
      action === 'pauseServer' || action === 'resumeServer'
        ? String(servers.data?.[0]?.id ?? '')
        : action === 'pauseCategory' || action === 'resumeCategory'
          ? (categories.data?.[0]?.name ?? '')
          : action === 'speedLimit'
            ? '1000'
            : ''
    setDraft({ ...draft, action, argument })
  }

  const toggleDay = (day: Day) =>
    setDraft({ ...draft, days: draft.days.includes(day) ? draft.days.filter((d) => d !== day) : everyDay.filter((d) => d === day || draft.days.includes(d)) })

  const argumentField = () => {
    switch (draft.action) {
      case 'speedLimit':
        return (
          <label>
            Speed limit
            <input className={`${ui.input} ${ui.mono}`} value={draft.argument} placeholder="1000 or 50%" onChange={(e) => setDraft({ ...draft, argument: e.target.value })} />
            <small className={styles.muted}>KB/s, or a percentage of the maximum line speed (Special). 0 = unlimited.</small>
          </label>
        )
      case 'pauseServer':
      case 'resumeServer':
        return (
          <label>
            Server
            <select className={ui.select} value={draft.argument} onChange={(e) => setDraft({ ...draft, argument: e.target.value })}>
              {(servers.data ?? []).map((s) => (
                <option key={s.id} value={String(s.id)}>
                  {s.name}
                </option>
              ))}
            </select>
          </label>
        )
      case 'pauseCategory':
      case 'resumeCategory':
        return (
          <label>
            Category
            <select className={ui.select} value={draft.argument} onChange={(e) => setDraft({ ...draft, argument: e.target.value })}>
              {(categories.data ?? []).map((c) => (
                <option key={c.id} value={c.name}>
                  {c.name}
                </option>
              ))}
            </select>
          </label>
        )
      default:
        return null
    }
  }

  const editor = (
    <div className={styles.editor}>
      <label>
        Time
        <input className={ui.input} type="time" value={draft.time} onChange={(e) => setDraft({ ...draft, time: e.target.value })} />
        {fieldErrors.time && <span className={styles.error}>{fieldErrors.time[0]}</span>}
      </label>
      <label>
        Action
        <select className={ui.select} value={draft.action} onChange={(e) => setAction(e.target.value as ScheduleAction)}>
          {actions.map((a) => (
            <option key={a.value} value={a.value}>
              {a.label}
            </option>
          ))}
        </select>
      </label>
      {argumentField()}
      <div className={styles.editorWide}>
        <div className={`${ui.seg} ${styles.days}`} role="group" aria-label="Days">
          {days.map((d) => (
            <button
              key={d.value}
              type="button"
              aria-pressed={draft.days.includes(d.value)}
              onClick={() => toggleDay(d.value)}
            >
              {d.label}
            </button>
          ))}
        </div>
        {fieldErrors.days && <span className={styles.error}>{fieldErrors.days[0]}</span>}
        {fieldErrors.argument && <span className={styles.error}>{fieldErrors.argument[0]}</span>}
      </div>
      <div className={styles.editorActions}>
        <span className={styles.error}>{formError}</span>
        <button className={ui.button} type="button" onClick={() => setEditing(null)} disabled={save.isPending}>
          Cancel
        </button>
        <button className={ui.primary} type="button" onClick={() => save.mutate()} disabled={save.isPending}>
          {save.isPending ? 'Saving…' : editing === 'new' ? 'Add rule' : 'Save'}
        </button>
      </div>
    </div>
  )

  const daysText = (rule: DownloadSchedule) =>
    rule.days.length === 7 ? 'Every day' : rule.days.map((d) => days.find((x) => x.value === d)?.label ?? d).join(', ')
  const what = (rule: DownloadSchedule) => {
    const name = actions.find((a) => a.value === rule.action)?.label ?? rule.action
    if (rule.action === 'speedLimit') return `${name}: ${rule.argument === '0' ? 'unlimited' : rule.argument.endsWith('%') ? rule.argument : `${rule.argument} KB/s`}`
    if (rule.action === 'pauseServer' || rule.action === 'resumeServer')
      return `${name}: ${servers.data?.find((s) => String(s.id) === rule.argument)?.name ?? `#${rule.argument}`}`
    if (rule.action === 'pauseCategory' || rule.action === 'resumeCategory') return `${name}: ${rule.argument}`
    return name
  }

  return (
    <Section
      id="scheduling"
      title="Scheduling"
      description="Rules run at the set time (server time). After a restart the latest rule of each kind is applied again."
      actions={
        editing === null && (
          <button className={`${ui.button} ${ui.sm}`} type="button" onClick={() => open(null)}>
            <Icon name="plus" size={14} />
            Add rule
          </button>
        )
      }
    >
      {rules.data && rules.data.length === 0 && editing === null && <p className={styles.empty}>No rules. Downloads run whenever something is in the queue.</p>}
      {rules.data && rules.data.length > 0 && (
        <div className={styles.tableWrap}>
        <table className={styles.table}>
          <thead>
            <tr>
              <th>On</th>
              <th>Time</th>
              <th>Days</th>
              <th>Action</th>
              <th aria-label="Actions" />
            </tr>
          </thead>
          <tbody>
            {rules.data.map((rule) => (
              <tr key={rule.id} className={rule.enabled ? undefined : styles.muted}>
                <td>
                  <Toggle checked={rule.enabled} label={`Rule at ${rule.time} on`} disabled={toggle.isPending} onChange={() => toggle.mutate(rule)} />
                </td>
                <td className={styles.name}>{rule.time}</td>
                <td>{daysText(rule)}</td>
                <td className={styles.cellWide}>{what(rule)}</td>
                <td>
                  <div className={styles.actions}>
                    <button className={`${ui.ghost} ${ui.sm}`} type="button" onClick={() => open(rule)}>
                      <Icon name="edit" size={14} />
                      Edit
                    </button>
                    <button
                      className={`${ui.ghost} ${ui.sm} ${ui.icon}`}
                      type="button"
                      aria-label={`Delete the rule at ${rule.time}`}
                      title="Delete"
                      disabled={remove.isPending}
                      onClick={() => remove.mutate(rule.id)}
                    >
                      <Icon name="trash" size={14} />
                    </button>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        </div>
      )}
      {editing !== null && editor}
    </Section>
  )
}
