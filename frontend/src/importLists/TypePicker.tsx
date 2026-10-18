import type { ImportListGroup, ImportListType } from '../api/importLists'
import ui from '../components/ui.module.css'
import connect from '../connect/Connect.module.css'
import Dialog from '../indexers/Dialog'
import { groupLabels, mediaLabel } from './labels'
import styles from './ImportLists.module.css'

const groups: ImportListGroup[] = ['tmdb', 'plex', 'trakt', 'other']

/** "Add list": the catalog, grouped by source. */
export default function TypePicker({ types, onSelect, onClose }: { types: ImportListType[]; onSelect: (type: ImportListType) => void; onClose: () => void }) {
  return (
    <Dialog
      title="Add import list"
      subtitle="Where StreamZH finds movies and series to add"
      size="wide"
      onClose={onClose}
      footer={
        <>
          <span className={connect.spacer} />
          <button className={ui.button} type="button" onClick={onClose}>
            Close
          </button>
        </>
      }
    >
      {groups.map((group) => {
        const list = types.filter((t) => t.group === group)
        if (list.length === 0) return null
        return (
          <section key={group} className={styles.group}>
            <h3 className={styles.groupTitle}>{groupLabels[group]}</h3>
            <ul className={connect.typeGrid}>
              {list.map((t) => (
                <li key={t.key}>
                  <button type="button" className={connect.typeCard} onClick={() => onSelect(t)}>
                    <span className={connect.typeName}>{t.name}</span>
                    <span className={connect.typeText}>{t.description}</span>
                    <span className={styles.typeMedia}>{t.mediaTypes.map(mediaLabel).join(' or ')}</span>
                  </button>
                </li>
              ))}
            </ul>
          </section>
        )
      })}
      <p className={styles.note}>
        IMDb lists are not offered: IMDb blocks automated access. Copy the list to TMDB or Trakt and add that one instead.
      </p>
    </Dialog>
  )
}
