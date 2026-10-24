import { useDeferredValue, useId, useMemo, useState } from 'react'
import type { MigrationAction, MigrationItem, MigrationPreview, MigrationSectionPreview } from '../api/migration'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import { AdvancedHint } from '../lib/Advanced'
import { advancedLabel } from '../lib/advancedMode'
import { actionLabels, countItems, plural, type Choices } from './choices'
import styles from './Migration.module.css'
import { choicesOf, defaultOf, optionsOf, secretFields } from './options'
import { rememberedUrl, rememberUrl, type SourceMeta } from './sources'

const pageSize = 100

/**
 * Step 4: what the old app has and what StreamZH would do with it, one collapsible panel per part with counts, options,
 * warnings and the items (searchable, 100 at a time) to leave single ones out or type in a password.
 */
export default function PreviewStep({
  source,
  preview,
  choices,
  onChoices,
  advanced,
  sideBySide,
  onEditPaths,
}: {
  source: SourceMeta
  preview: MigrationPreview
  choices: Choices
  onChoices: (update: (c: Choices) => Choices) => void
  advanced: boolean
  sideBySide: boolean
  onEditPaths: (() => void) | null
}) {
  const sections = useMemo(() => [...preview.sections].sort((a, b) => a.order - b.order), [preview.sections])
  const visible = sections.filter((s) => advanced || !s.advanced)
  const hidden = sections.length - visible.length
  const [open, setOpen] = useState<Set<string>>(new Set())
  const reachable = preview.paths.filter((p) => p.exists).length

  const toggle = (set: Set<string>, key: string, on: boolean) => {
    const next = new Set(set)
    if (on) next.add(key)
    else next.delete(key)
    return next
  }

  return (
    <div className={styles.stepBody}>
      <div className={styles.appLine}>
        <span className={`${ui.pill} ${ui.pillOk}`}>
          <Icon name="check" size={13} />
          {preview.app.appName} {preview.app.version}
        </span>
        {preview.paths.length > 0 && (
          <span className={styles.appMeta}>
            {reachable === preview.paths.length
              ? `${plural(preview.paths.length, 'folder')} found`
              : `${reachable} of ${plural(preview.paths.length, 'folder')} found`}
            {onEditPaths && (
              <>
                {' · '}
                <button type="button" className={styles.linkBtn} onClick={onEditPaths}>
                  Path mappings
                </button>
              </>
            )}
          </span>
        )}
      </div>

      {preview.warnings.length > 0 && (
        <div className={styles.warnBox} role="note">
          <Icon name="alert" size={15} />
          <div>
            {preview.warnings.map((w) => (
              <p key={w}>{w}</p>
            ))}
          </div>
        </div>
      )}

      {visible.length === 0 ? (
        <p className={ui.empty}>{source.label} has nothing StreamZH can take over yet.</p>
      ) : (
        <ul className={styles.sections}>
          {visible.map((section) => (
            <SectionPanel
              key={section.key}
              source={source}
              section={section}
              selected={choices.selected.has(section.key)}
              onSelect={(on) => onChoices((c) => ({ ...c, selected: toggle(c.selected, section.key, on) }))}
              open={open.has(section.key)}
              onOpen={(on) => setOpen((o) => toggle(o, section.key, on))}
              choices={choices}
              onChoices={onChoices}
              advanced={advanced}
              sideBySide={sideBySide}
            />
          ))}
        </ul>
      )}
      <AdvancedHint count={hidden} />
    </div>
  )
}

const actionClass: Record<MigrationAction, string> = {
  create: styles.actCreate,
  update: styles.actUpdate,
  link: styles.actLink,
  skip: styles.actSkip,
}

function Counts({ counts }: { counts: ReturnType<typeof countItems> }) {
  const parts: [MigrationAction | 'out', number, string][] = [
    ['create', counts.create, 'new'],
    ['update', counts.update, 'update'],
    ['link', counts.link, 'link'],
    ['skip', counts.skip, 'skip'],
    ['out', counts.out, 'left out'],
  ]
  return (
    <span className={styles.counts}>
      {parts
        .filter(([, n]) => n > 0)
        .map(([kind, n, label]) => (
          <span key={kind} className={`${styles.count} ${kind === 'out' ? styles.actSkip : actionClass[kind]}`}>
            <b className={ui.num}>{n.toLocaleString()}</b> {label}
          </span>
        ))}
    </span>
  )
}

function SectionPanel({
  source,
  section,
  selected,
  onSelect,
  open,
  onOpen,
  choices,
  onChoices,
  advanced,
  sideBySide,
}: {
  source: SourceMeta
  section: MigrationSectionPreview
  selected: boolean
  onSelect: (on: boolean) => void
  open: boolean
  onOpen: (on: boolean) => void
  choices: Choices
  onChoices: (update: (c: Choices) => Choices) => void
  advanced: boolean
  sideBySide: boolean
}) {
  const id = useId()
  // Prowlarr's indexers: the secret is the indexer's API key (they are reached at their own address, never through Prowlarr).
  const optionalSecret = source.key === 'prowlarr'
  const counts = countItems(section.items, choices.excluded)
  const secrets = section.items.filter((i) => i.needsSecret && !choices.excluded.has(i.key) && i.action !== 'skip')
  const missingSecrets = secrets.filter((i) => !choices.secrets[i.key]).length
  const options = optionsOf(section.key).filter(([, o]) => advanced || !o.advanced)
  const failed = section.items.length === 0 && section.warnings.length > 0

  return (
    <li className={`${styles.section} ${selected ? styles.sectionOn : ''}`}>
      <div className={styles.sectionHead}>
        <label className={styles.sectionCheck}>
          <input type="checkbox" checked={selected} onChange={(e) => onSelect(e.target.checked)} aria-describedby={`${id}-desc`} />
          <span className={`${styles.sectionLabel} ${section.advanced ? advancedLabel : ''}`}>{section.label}</span>
        </label>
        <span className={styles.headMeta}>
          <Counts counts={counts} />
          {selected && missingSecrets > 0 && (
            <span className={`${ui.pill} ${ui.pillWarn}`}>
              <Icon name="key" size={12} />
              {missingSecrets} {optionalSecret ? (missingSecrets === 1 ? 'API key' : 'API keys') : missingSecrets === 1 ? 'password' : 'passwords'}
            </span>
          )}
          {section.warnings.length > 0 && (
            <span className={styles.warnDot} title={section.warnings.join('\n')}>
              <Icon name="alert" size={14} />
              <span className={styles.srOnly}>{plural(section.warnings.length, 'warning')}</span>
            </span>
          )}
        </span>
        {section.items.length > 0 && (
          <button
            type="button"
            className={`${ui.ghost} ${ui.sm} ${styles.expand}`}
            aria-expanded={open}
            aria-controls={`${id}-body`}
            onClick={() => onOpen(!open)}
          >
            {section.items.length > 0 ? (open ? 'Hide' : `Show ${section.items.length.toLocaleString()}`) : open ? 'Hide' : 'Details'}
            <Icon name="down" size={14} className={open ? styles.chevOpen : styles.chev} />
          </button>
        )}
      </div>
      <p id={`${id}-desc`} className={styles.sectionDesc}>
        {section.description}
      </p>
      {selected && options.length > 0 && (
        <div className={styles.options}>
          {options.map(([key, option]) => {
            const value = choices.options[key] ?? defaultOf(option, { sideBySide })
            const set = (v: string) => onChoices((c) => ({ ...c, options: { ...c.options, [key]: v } }))
            return option.kind === 'toggle' ? (
              <label key={key} className={`${styles.option} ${styles.optionToggle}`}>
                <input type="checkbox" className={styles.itemCheck} checked={value === 'true'} onChange={(e) => set(String(e.target.checked))} />
                <span className={option.advanced ? advancedLabel : undefined}>{option.label}</span>
                {option.help && <small>{option.help}</small>}
              </label>
            ) : (
            <label key={key} className={styles.option}>
              <span className={option.advanced ? advancedLabel : undefined}>{option.label}</span>
              {option.kind === 'choice' ? (
                <select
                  className={`${ui.select} ${styles.optionControl}`}
                  value={value}
                  onChange={(e) => set(e.target.value)}
                >
                  {choicesOf(option, source.key).map((c) => (
                    <option key={c.value} value={c.value}>
                      {c.label}
                    </option>
                  ))}
                </select>
              ) : (
                <span className={styles.withUnit}>
                  <input
                    className={`${ui.input} ${ui.num} ${styles.optionNumber}`}
                    type="number"
                    min={option.min}
                    max={option.max}
                    value={value}
                    onChange={(e) => set(e.target.value)}
                  />
                  {option.unit}
                </span>
              )}
              {option.help && <small>{option.help}</small>}
            </label>
            )
          })}
        </div>
      )}
      {(open || failed) && section.warnings.length > 0 && (
        <ul className={styles.sectionWarnings}>
          {section.warnings.map((w) => (
            <li key={w}>
              <Icon name="alert" size={13} />
              {w}
            </li>
          ))}
        </ul>
      )}
      {open && (
        <div id={`${id}-body`} className={styles.sectionBody}>
          {section.items.length > 0 ? (
            <ItemList section={section} disabled={!selected} choices={choices} onChoices={onChoices} optionalSecret={optionalSecret} />
          ) : (
            !failed && <p className={styles.mapEmpty}>Nothing to take over in this part.</p>
          )}
        </div>
      )}
    </li>
  )
}

type Filter = 'all' | MigrationAction

function ItemList({
  section,
  disabled,
  choices,
  onChoices,
  optionalSecret,
}: {
  section: MigrationSectionPreview
  disabled: boolean
  choices: Choices
  onChoices: (update: (c: Choices) => Choices) => void
  optionalSecret: boolean
}) {
  const [query, setQuery] = useState('')
  const [filter, setFilter] = useState<Filter>('all')
  const [limit, setLimit] = useState(pageSize)
  const q = useDeferredValue(query.trim().toLowerCase())

  // Items that need a password first, so nobody has to scroll for them.
  const items = useMemo(() => {
    const list = section.items.filter(
      (i) => (filter === 'all' || i.action === filter) && (!q || i.label.toLowerCase().includes(q) || (i.detail ?? '').toLowerCase().includes(q)),
    )
    return list.sort((a, b) => Number(!!b.needsSecret) - Number(!!a.needsSecret))
  }, [section.items, filter, q])
  const byAction = useMemo(() => countItems(section.items, new Set()), [section.items])
  const shown = items.slice(0, limit)
  const takeable = items.filter((i) => i.action !== 'skip')

  const setExcluded = (keys: string[], out: boolean) =>
    onChoices((c) => {
      const excluded = new Set(c.excluded)
      for (const key of keys) {
        if (out) excluded.add(key)
        else excluded.delete(key)
      }
      return { ...c, excluded }
    })

  const filters: [Filter, string, number][] = [
    ['all', 'All', section.items.length],
    ['create', 'New', byAction.create],
    ['update', 'Update', byAction.update],
    ['link', 'Link', byAction.link],
    ['skip', 'Skip', byAction.skip],
  ]

  return (
    <div className={styles.items}>
      {section.items.length > 8 && (
        <div className={styles.itemTools}>
          <div className={ui.seg} role="group" aria-label="Show">
            {filters
              .filter(([key, , n]) => key === 'all' || n > 0)
              .map(([key, label, n]) => (
                <button key={key} type="button" aria-pressed={filter === key} onClick={() => (setFilter(key), setLimit(pageSize))}>
                  {label} <span className={styles.segCount}>{n.toLocaleString()}</span>
                </button>
              ))}
          </div>
          <span className={styles.search}>
            <Icon name="search" size={15} />
            <input
              className={ui.input}
              type="search"
              placeholder="Search"
              aria-label={`Search ${section.label}`}
              value={query}
              onChange={(e) => (setQuery(e.target.value), setLimit(pageSize))}
            />
          </span>
          {(q || filter !== 'all') && takeable.length > 0 && !disabled && (
            <span className={styles.bulk}>
              <button type="button" className={`${ui.ghost} ${ui.sm}`} onClick={() => setExcluded(takeable.map((i) => i.key), false)}>
                Take all {takeable.length.toLocaleString()}
              </button>
              <button type="button" className={`${ui.ghost} ${ui.sm}`} onClick={() => setExcluded(takeable.map((i) => i.key), true)}>
                Leave all out
              </button>
            </span>
          )}
        </div>
      )}
      {items.length === 0 ? (
        <p className={styles.mapEmpty}>Nothing matches.</p>
      ) : (
        <ul className={styles.itemList}>
          {shown.map((item) => (
            <ItemRow
              key={item.key}
              item={item}
              section={section.key}
              optionalSecret={optionalSecret}
              disabled={disabled}
              excluded={choices.excluded.has(item.key)}
              secrets={choices.secrets}
              onExclude={(out) => setExcluded([item.key], out)}
              onSecret={(key, value) => onChoices((c) => ({ ...c, secrets: { ...c.secrets, [key]: value } }))}
            />
          ))}
        </ul>
      )}
      {items.length > shown.length && (
        <div className={styles.more}>
          <span className={ui.muted}>
            {shown.length.toLocaleString()} of {items.length.toLocaleString()}
          </span>
          <button type="button" className={`${ui.button} ${ui.sm}`} onClick={() => setLimit(limit + pageSize)}>
            Show {Math.min(pageSize, items.length - shown.length)} more
          </button>
        </div>
      )}
    </div>
  )
}

function ItemRow({
  item,
  section,
  optionalSecret,
  disabled,
  excluded,
  secrets,
  onExclude,
  onSecret,
}: {
  item: MigrationItem
  section: string
  optionalSecret: boolean
  disabled: boolean
  excluded: boolean
  secrets: Record<string, string>
  onExclude: (out: boolean) => void
  onSecret: (key: string, value: string) => void
}) {
  const secret = secrets[item.key] ?? ''
  // Connections with more than one hidden field (Pushover: API key and user key).
  const multi = section === 'notifications' ? secretFields[(item.detail ?? '').split(' · ')[0].trim().toLowerCase()] : undefined
  const firstLabel = multi?.first ?? (optionalSecret ? 'API key' : 'Password or API key')
  const skip = item.action === 'skip'
  const taken = !skip && !excluded
  // Radarr/Sonarr's SABnzbd client: its address can be used for the SABnzbd step.
  const sabUrl = section === 'downloadClients' && item.detail && /^https?:\/\//i.test(item.detail) ? item.detail : null
  const [sabSaved, setSabSaved] = useState(() => !!sabUrl && rememberedUrl('sabnzbd') === sabUrl)
  return (
    <li className={`${styles.item} ${!taken || disabled ? styles.itemOff : ''}`}>
      <input
        type="checkbox"
        className={styles.itemCheck}
        checked={taken}
        disabled={skip || disabled}
        aria-label={`Take over ${item.label}`}
        onChange={(e) => onExclude(!e.target.checked)}
      />
      <div className={styles.itemMain}>
        <span className={styles.itemLabel}>{item.label}</span>
        {item.detail && <span className={styles.itemDetail}>{item.detail}</span>}
        {skip && item.reason && <span className={styles.itemDetail}>{item.reason}</span>}
        {sabUrl && (
          <span className={styles.sabLine}>
            {sabSaved ? (
              <span className={styles.sabSaved}>
                <Icon name="check" size={12} />
                The SABnzbd step starts with this address.
              </span>
            ) : (
              <button
                type="button"
                className={`${ui.button} ${ui.sm}`}
                onClick={() => {
                  rememberUrl('sabnzbd', sabUrl)
                  setSabSaved(true)
                }}
              >
                <Icon name="download" size={13} />
                Use for SABnzbd
              </button>
            )}
          </span>
        )}
        {item.warning && (
          <span className={styles.itemWarn}>
            <Icon name="alert" size={12} />
            {item.warning}
          </span>
        )}
        {item.needsSecret && taken && !disabled && (
          <span className={styles.secret}>
            <input
              className={`${ui.input} ${styles.secretInput}`}
              type="password"
              autoComplete="new-password"
              placeholder={firstLabel}
              aria-label={optionalSecret ? `API key for ${item.label}` : multi ? `${multi.first} for ${item.label}` : `Password for ${item.label}`}
              value={secret}
              onChange={(e) => onSecret(item.key, e.target.value)}
            />
            {multi?.more.map((f) => (
              <input
                key={f.field}
                className={`${ui.input} ${styles.secretInput}`}
                type="password"
                autoComplete="new-password"
                placeholder={f.label}
                aria-label={`${f.label} for ${item.label}`}
                value={secrets[`${item.key}.${f.field}`] ?? ''}
                onChange={(e) => onSecret(`${item.key}.${f.field}`, e.target.value)}
              />
            ))}
            <small>
              {secret
                ? 'Stored in StreamZH like one you type into its own form.'
                : optionalSecret
                  ? 'Prowlarr does not hand it out. Without it, the indexer is added switched off until you enter it.'
                  : 'The old app does not hand it out. Without it, this is added switched off.'}
            </small>
          </span>
        )}
      </div>
      <span className={`${styles.act} ${actionClass[item.action]}`}>{actionLabels[item.action]}</span>
    </li>
  )
}
