import { useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { generalApi, generalKeys, type UiSettings } from '../api/general'
import ui from '../components/ui.module.css'
import { formatDate, formatDateTime, formatLongDate, formatTime, relativeTime, setUiFormats } from '../lib/format'
import styles from './General.module.css'
import { Row, SaveBar, Section, Select, SettingsPage, Toggle } from './SettingsKit'
import { useSettingsForm } from './useSettingsForm'

// A fixed afternoon, so 12 h and 24 h look different in the examples.
const sample = new Date(2026, 9, 17, 14, 5)

/** "Oct 17, 2026" or "Browser default (Oct 17, 2026)". */
function label(format: string, example: string): string {
  return format === 'locale' ? `Browser default (${example})` : example
}

/** Settings → UI: date and time formats for everyone on this StreamZH (like Radarr's UI settings). */
export default function UiPage() {
  const queryClient = useQueryClient()
  const form = useSettingsForm<UiSettings>({
    queryKey: generalKeys.ui,
    load: generalApi.ui,
    save: async (draft) => {
      const saved = await generalApi.saveUi({
        shortDateFormat: draft.shortDateFormat,
        longDateFormat: draft.longDateFormat,
        timeFormat: draft.timeFormat,
        showRelativeDates: draft.showRelativeDates,
      })
      setUiFormats(saved)
      queryClient.setQueryData(generalKeys.ui, saved)
      return saved
    },
  })
  const v = form.values
  const [tenMinutesAgo] = useState(() => new Date(Date.now() - 10 * 60_000))

  return (
    <SettingsPage title="UI" lead="How dates and times are shown, for everyone on this StreamZH.">
      {form.query.isError && <p className={ui.error}>{form.query.error.message}</p>}
      {v && (
        <>
          <Section
            id="dates"
            title="Dates and times"
            footer={<SaveBar dirty={form.dirty} saving={form.saving} saved={form.saved} error={form.formError} onSave={form.save} onReset={form.reset} />}
          >
            <Row label="Short date" htmlFor="shortDateFormat" error={form.error('shortDateFormat')} help="Lists, history, file dates.">
              <Select
                id="shortDateFormat"
                value={v.shortDateFormat}
                onChange={(shortDateFormat) => form.set({ shortDateFormat })}
                options={v.shortDateFormats.map((f) => ({ value: f, label: label(f, formatDate(sample, { ...v, shortDateFormat: f })) }))}
              />
            </Row>
            <Row label="Long date" htmlFor="longDateFormat" error={form.error('longDateFormat')} help="Where there is room for the weekday.">
              <Select
                id="longDateFormat"
                value={v.longDateFormat}
                onChange={(longDateFormat) => form.set({ longDateFormat })}
                options={v.longDateFormats.map((f) => ({ value: f, label: label(f, formatLongDate(sample, { ...v, longDateFormat: f })) }))}
              />
            </Row>
            <Row label="Time format" htmlFor="timeFormat" error={form.error('timeFormat')}>
              <Select
                id="timeFormat"
                value={v.timeFormat}
                onChange={(timeFormat) => form.set({ timeFormat })}
                options={v.timeFormats.map((f) => ({
                  value: f,
                  label: f === 'locale' ? label(f, formatTime(sample, { ...v, timeFormat: f })) : f === 'HH:mm' ? '24 hours (14:05)' : '12 hours (2:05 PM)',
                }))}
              />
            </Row>
            <Row label="Relative dates" help="“3 min ago” instead of the date and time for recent events. Off: always the date and time.">
              <Toggle checked={v.showRelativeDates} onChange={(showRelativeDates) => form.set({ showRelativeDates })} />
            </Row>
            <Row label="Preview">
              <dl className={styles.preview}>
                <dt>Short date</dt>
                <dd>{formatDate(sample, v)}</dd>
                <dt>Long date</dt>
                <dd>{formatLongDate(sample, v)}</dd>
                <dt>Date and time</dt>
                <dd>{formatDateTime(sample, v)}</dd>
                <dt>Ten minutes ago</dt>
                <dd>{relativeTime(tenMinutesAgo, v)}</dd>
              </dl>
            </Row>
          </Section>

          <Section id="style" title="Style and language">
            <Row label="Theme" help="A light theme is planned.">
              <span className={styles.value}>Dark</span>
            </Row>
            <Row label="UI language" help="German follows in a later version. Titles and descriptions follow Settings → Metadata.">
              <span className={styles.value}>English</span>
            </Row>
          </Section>
        </>
      )}
    </SettingsPage>
  )
}
