import { useState } from 'react'
import type { RuleType } from '../../api/renamer'
import Icon from '../../components/Icon'
import ui from '../../components/ui.module.css'
import styles from './Renamer.module.css'
import { CaseForm, CounterForm, ExtensionForm, InsertForm, MediaForm, RemoveForm, ReplaceForm, TrimForm } from './RuleForms'
import { newRule, ruleLabel, ruleSummary, ruleTypes, starters, type EditorRule } from './rules'

/**
 * The ordered rules: each one can be switched off, moved, removed and opened for editing. Rules run from top to bottom,
 * each on the name the one above built.
 */
export default function RuleList({
  rules,
  onChange,
  errors,
  sample,
  settingsFormats,
}: {
  rules: EditorRule[]
  onChange: (rules: EditorRule[]) => void
  errors: Record<string, string>
  sample: string | null
  settingsFormats: { episode: string; movie: string } | null
}) {
  const [open, setOpen] = useState<Set<string>>(() => new Set(rules.length === 1 ? [rules[0].id] : []))
  const [adding, setAdding] = useState(false)

  const toggle = (id: string) =>
    setOpen((s) => {
      const next = new Set(s)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })

  const update = (id: string, patch: Partial<EditorRule>) => onChange(rules.map((r) => (r.id === id ? ({ ...r, ...patch } as EditorRule) : r)))
  const move = (index: number, by: number) => {
    const next = [...rules]
    const [rule] = next.splice(index, 1)
    next.splice(index + by, 0, rule)
    onChange(next)
  }
  const add = (type: RuleType) => {
    const rule = newRule(type)
    onChange([...rules, rule])
    setOpen((s) => new Set(s).add(rule.id))
    setAdding(false)
  }

  return (
    <div className={styles.rules}>
      {rules.length === 0 && (
        <div className={styles.starters}>
          <p className={styles.startersText}>No rules yet. Start with one of these, or add rules one by one.</p>
          <div className={styles.starterList}>
            {starters.map((s) => (
              <button
                key={s.label}
                type="button"
                className={styles.starter}
                onClick={() => {
                  const next = s.rules()
                  onChange(next)
                  setOpen(new Set(next.length === 1 ? [next[0].id] : []))
                }}
              >
                <strong>{s.label}</strong>
                <small>{s.hint}</small>
              </button>
            ))}
          </div>
        </div>
      )}

      <ol className={styles.ruleList}>
        {rules.map((rule, index) => {
          const error = errors[String(index)]
          const isOpen = open.has(rule.id)
          const stem = sample && rule.type === 'replace' && rule.scope === 'name' ? sample.replace(/\.[^.]+$/, '') : sample
          const set = (patch: Partial<EditorRule>) => update(rule.id, patch)
          return (
            <li key={rule.id} className={`${styles.rule} ${isOpen ? styles.ruleOpen : ''} ${!rule.enabled ? styles.ruleOff : ''} ${error ? styles.ruleInvalid : ''} ${rule.type === 'media' ? styles.ruleMedia : ''}`}>
              <div className={styles.ruleHead}>
                <span className={styles.ruleNumber} aria-hidden="true">
                  {index + 1}
                </span>
                <button type="button" className={styles.ruleTitle} aria-expanded={isOpen} onClick={() => toggle(rule.id)}>
                  <span className={styles.ruleType}>{ruleLabel(rule.type)}</span>
                  <span className={styles.ruleSummary}>{ruleSummary(rule)}</span>
                  <span className={styles.chevron} aria-hidden="true">
                    <Icon name="down" size={15} />
                  </span>
                </button>
                <div className={styles.ruleTools}>
                  <label className={styles.ruleSwitch} title={rule.enabled ? 'Switch the rule off' : 'Switch the rule on'}>
                    <input type="checkbox" role="switch" checked={rule.enabled} onChange={(e) => update(rule.id, { enabled: e.target.checked })} aria-label={`Rule ${index + 1} on`} />
                    <span aria-hidden="true" />
                  </label>
                  <button type="button" className={styles.iconButton} disabled={index === 0} onClick={() => move(index, -1)} aria-label={`Move rule ${index + 1} up`} title="Up">
                    <Icon name="up" size={15} />
                  </button>
                  <button type="button" className={styles.iconButton} disabled={index === rules.length - 1} onClick={() => move(index, 1)} aria-label={`Move rule ${index + 1} down`} title="Down">
                    <Icon name="down" size={15} />
                  </button>
                  <button type="button" className={styles.iconButton} onClick={() => onChange(rules.filter((r) => r.id !== rule.id))} aria-label={`Remove rule ${index + 1}`} title="Remove">
                    <Icon name="x" size={15} />
                  </button>
                </div>
              </div>
              {error && <p className={styles.ruleError}>{error}</p>}
              {isOpen && (
                <div className={styles.ruleBody}>
                  {rule.type === 'replace' && <ReplaceForm rule={rule} set={set} sample={stem} />}
                  {rule.type === 'remove' && <RemoveForm rule={rule} set={set} sample={stem} />}
                  {rule.type === 'insert' && <InsertForm rule={rule} set={set} sample={stem} />}
                  {rule.type === 'case' && <CaseForm rule={rule} set={set} sample={stem} />}
                  {rule.type === 'counter' && <CounterForm rule={rule} set={set} sample={stem} />}
                  {rule.type === 'extension' && <ExtensionForm rule={rule} set={set} sample={stem} />}
                  {rule.type === 'trim' && <TrimForm rule={rule} set={set} sample={stem} />}
                  {rule.type === 'media' && <MediaForm rule={rule} set={set} sample={stem} settingsFormats={settingsFormats} />}
                </div>
              )}
            </li>
          )
        })}
      </ol>

      {adding ? (
        <div className={styles.addMenu} role="menu">
          {ruleTypes.map((t) => (
            <button key={t.type} type="button" role="menuitem" className={styles.addItem} onClick={() => add(t.type)}>
              <strong>{t.label}</strong>
              <small>{t.hint}</small>
            </button>
          ))}
          <button type="button" className={ui.button} onClick={() => setAdding(false)}>
            Cancel
          </button>
        </div>
      ) : (
        <button type="button" className={`${ui.button} ${styles.addButton}`} onClick={() => setAdding(true)} disabled={rules.length >= 50}>
          <Icon name="plus" size={15} />
          Add rule
        </button>
      )}
    </div>
  )
}
