import type { MigrationPathCheck, PathMapping } from '../api/migration'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import styles from './Migration.module.css'
import type { SourceMeta } from './sources'

/**
 * Step 3 (only when needed): the folders the old app uses, as StreamZH sees them, and an editor for path mappings
 * (/data/media/tv in a Docker container → /mnt/media/tv here). "Check again" reads the preview with the new mappings.
 */
export default function PathsStep({
  source,
  paths,
  mappings,
  onMappings,
  busy,
  error,
  onCheck,
  onBack,
  onContinue,
}: {
  source: SourceMeta
  paths: MigrationPathCheck[]
  mappings: PathMapping[]
  onMappings: (mappings: PathMapping[]) => void
  busy: boolean
  error: string | null
  onCheck: () => void
  onBack: () => void
  onContinue: () => void
}) {
  const missing = paths.filter((p) => !p.exists)
  const set = (index: number, change: Partial<PathMapping>) => onMappings(mappings.map((m, i) => (i === index ? { ...m, ...change } : m)))
  const add = (remote = '') => onMappings([...mappings, { remote, local: '' }])
  const unmappedMissing = missing.filter((p) => !mappings.some((m) => m.remote && p.remote.startsWith(m.remote)))

  return (
    <div className={styles.stepBody}>
      <p className={styles.stepLead}>
        If {source.label} runs in Docker or on another machine, its folders have other names here: tell StreamZH where the same folders are.
      </p>

      <div className={styles.pathTable} role="table" aria-label="Folders">
        <div className={styles.pathHead} role="row">
          <span role="columnheader">In {source.label}</span>
          <span role="columnheader">In StreamZH</span>
          <span role="columnheader" className={styles.srOnly}>
            Found
          </span>
        </div>
        {paths.map((p) => (
          <div key={p.remote} className={styles.pathRow} role="row">
            <span role="cell" className={`${ui.mono} ${styles.pathCell}`} title={p.remote}>
              {p.remote}
            </span>
            <span role="cell" className={`${ui.mono} ${styles.pathCell}`} title={p.local}>
              <Icon name="right" size={13} className={styles.pathArrow} />
              {p.local}
            </span>
            <span role="cell" className={styles.pathState}>
              {p.exists ? (
                <span className={`${ui.pill} ${ui.pillOk}`}>
                  <Icon name="check" size={13} />
                  Found
                </span>
              ) : (
                <span className={`${ui.pill} ${ui.pillWarn}`}>
                  <Icon name="alert" size={13} />
                  Not found
                </span>
              )}
            </span>
          </div>
        ))}
      </div>

      <div className={styles.mapBlock}>
        <div className={styles.mapHead}>
          <h3 className={styles.miniTitle}>Path mappings</h3>
          {unmappedMissing.length > 0 && (
            <div className={styles.mapQuick}>
              {unmappedMissing.slice(0, 4).map((p) => (
                <button key={p.remote} type="button" className={`${ui.button} ${ui.sm}`} onClick={() => add(p.remote)}>
                  <Icon name="plus" size={13} />
                  Map <span className={ui.mono}>{p.remote}</span>
                </button>
              ))}
            </div>
          )}
        </div>
        {mappings.length === 0 ? (
          <p className={styles.mapEmpty}>No mappings. Add one for each folder StreamZH does not find.</p>
        ) : (
          <ul className={styles.mapList}>
            {mappings.map((m, i) => (
              <li key={i} className={styles.mapRow}>
                <input
                  className={`${ui.input} ${ui.mono}`}
                  aria-label={`Folder in ${source.label}`}
                  placeholder="/data/media/tv"
                  spellCheck={false}
                  value={m.remote}
                  onChange={(e) => set(i, { remote: e.target.value })}
                />
                <Icon name="right" size={15} className={styles.mapArrow} />
                <input
                  className={`${ui.input} ${ui.mono}`}
                  aria-label="Same folder in StreamZH"
                  placeholder="/mnt/media/tv"
                  spellCheck={false}
                  value={m.local}
                  onChange={(e) => set(i, { local: e.target.value })}
                />
                <button
                  type="button"
                  className={`${ui.ghost} ${ui.icon} ${ui.sm}`}
                  aria-label="Remove mapping"
                  onClick={() => onMappings(mappings.filter((_, j) => j !== i))}
                >
                  <Icon name="trash" size={15} />
                </button>
              </li>
            ))}
          </ul>
        )}
        <div className={styles.mapActions}>
          <button type="button" className={`${ui.ghost} ${ui.sm}`} onClick={() => add()}>
            <Icon name="plus" size={14} />
            Add mapping
          </button>
          <button type="button" className={`${ui.button} ${ui.sm}`} onClick={onCheck} disabled={busy}>
            <Icon name="restart" size={14} />
            {busy ? 'Checking…' : 'Check again'}
          </button>
        </div>
      </div>

      {error && (
        <p className={ui.errBox} role="alert">
          <Icon name="alert" size={15} />
          {error}
        </p>
      )}

      <div className={styles.stepActions}>
        <button type="button" className={ui.ghost} onClick={onBack}>
          <Icon name="left" size={15} />
          Back
        </button>
        <span className={styles.grow} />
        {missing.length > 0 && <span className={styles.actionNote}>Files in folders StreamZH cannot see are not linked.</span>}
        <button type="button" className={missing.length > 0 ? ui.button : ui.primary} onClick={onContinue} disabled={busy}>
          {missing.length > 0 ? 'Continue anyway' : 'Continue'}
          <Icon name="right" size={15} />
        </button>
      </div>
    </div>
  )
}
