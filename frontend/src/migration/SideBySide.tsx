import { useState } from 'react'
import { ApiError } from '../api/client'
import Icon from '../components/Icon'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import { formatDate } from '../lib/format'
import { Row, Section, Toggle } from '../settings/SettingsKit'
import { useAutomation, useSetAutomation } from './hooks'
import styles from './Migration.module.css'
import { utc } from './sources'

// Side by side (0.9): after a migration StreamZH runs next to the old apps, takes everything over but grabs nothing by
// itself until the owner switches over. The top bar shows a pill while it is on; Settings → Migration has the switch.

const since = (value: string | null | undefined) => (value ? ` since ${formatDate(utc(value))}` : '')

/** The pill in the top bar while side by side is on; opens the switch-over dialog. */
export function SideBySidePill() {
  const automation = useAutomation()
  const [confirm, setConfirm] = useState(false)
  if (!automation.data?.hold) return null
  return (
    <>
      <button
        type="button"
        className={styles.sbsPill}
        onClick={() => setConfirm(true)}
        title={`StreamZH runs next to your old apps${since(automation.data.since)} and grabs nothing automatically.`}
      >
        <span className={styles.sbsDot} aria-hidden="true" />
        <b>Side by side</b>
        <span className={styles.sbsText}>· grabs nothing automatically</span>
        <span className={styles.sbsAct}>Switch over…</span>
      </button>
      {confirm && <SwitchOverDialog since={automation.data.since} onClose={() => setConfirm(false)} />}
    </>
  )
}

/** Confirms the switch-over: what changes, and what to turn off in the old apps first. */
export function SwitchOverDialog({ since: from, onClose }: { since: string | null; onClose: () => void }) {
  const set = useSetAutomation()
  const error = set.error instanceof ApiError ? set.error.message : set.error ? String(set.error) : null
  return (
    <Modal title="Switch over to StreamZH?" onClose={onClose}>
      <div className={styles.dialogText}>
        <p>
          StreamZH runs side by side with your old apps{since(from)}: it keeps its library and settings up to date but grabs nothing
          by itself.
        </p>
        <h3 className={ui.eyebrow}>When you switch over</h3>
        <ul className={styles.checkList}>
          <li>
            <Icon name="zap" size={15} />
            <span>StreamZH searches, grabs and upgrades on its own: RSS sync, searches for missing items and import lists.</span>
          </li>
          <li>
            <Icon name="download" size={15} />
            <span>What it grabs is downloaded by its own Usenet engine.</span>
          </li>
        </ul>
        <h3 className={ui.eyebrow}>First, in the old apps</h3>
        <ul className={styles.checkList}>
          <li>
            <Icon name="check" size={15} />
            <span>
              Radarr and Sonarr: turn off <b>RSS</b> and <b>automatic search</b> on every indexer (Settings → Indexers), so nothing is
              grabbed twice.
            </span>
          </li>
          <li>
            <Icon name="check" size={15} />
            <span>SABnzbd: let its queue finish, then pause it.</span>
          </li>
        </ul>
        <p className={ui.muted}>You can turn side by side back on in Settings → Migration.</p>
      </div>
      {error && (
        <p className={ui.errBox} role="alert">
          <Icon name="alert" size={15} />
          {error}
        </p>
      )}
      <div className={styles.dialogActions}>
        <button type="button" className={ui.ghost} onClick={onClose}>
          Cancel
        </button>
        <button
          type="button"
          className={ui.primary}
          disabled={set.isPending}
          onClick={() => set.mutate(false, { onSuccess: onClose })}
        >
          <Icon name="play" size={15} />
          {set.isPending ? 'Switching over…' : 'Switch over'}
        </button>
      </div>
    </Modal>
  )
}

/** Settings → Migration: the switch itself. Turning it off goes through the same dialog as the pill. */
export function SideBySideSection() {
  const automation = useAutomation()
  const set = useSetAutomation()
  const [confirm, setConfirm] = useState(false)
  if (automation.isError || !automation.data) return null
  const hold = automation.data.hold
  return (
    <Section
      id="side-by-side"
      title="Side by side"
      description="Run StreamZH next to your old apps until you are happy with it. Your library and settings stay up to date; nothing is grabbed automatically."
    >
      <Row
        label="Grab nothing automatically"
        help={
          hold
            ? `On${since(automation.data.since)}. Searching and grabbing by hand still works.`
            : 'Off: StreamZH searches and grabs on its own.'
        }
        error={set.error instanceof ApiError ? set.error.message : undefined}
      >
        <Toggle checked={hold} disabled={set.isPending} onChange={(on) => (on ? set.mutate(true) : setConfirm(true))} />
      </Row>
      {confirm && <SwitchOverDialog since={automation.data.since} onClose={() => setConfirm(false)} />}
    </Section>
  )
}
