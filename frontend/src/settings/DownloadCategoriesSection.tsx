import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { ApiError } from '../api/client'
import {
  downloadSettingsApi,
  downloadSettingsKeys,
  type CategoryPriority,
  type DownloadCategory,
  type DownloadCategoryInput,
  type PostProcessLevel,
} from '../api/downloadSettings'
import ui from '../components/ui.module.css'
import styles from './DownloadsPage.module.css'
import { Section } from './SettingsKit'

const categoryPriorities: { value: CategoryPriority; label: string }[] = [
  { value: 'default', label: 'Default' },
  { value: 'paused', label: 'Paused' },
  { value: 'low', label: 'Low' },
  { value: 'normal', label: 'Normal' },
  { value: 'high', label: 'High' },
  { value: 'force', label: 'Force' },
]

const postProcessLevels: { value: PostProcessLevel; label: string }[] = [
  { value: 'none', label: 'None' },
  { value: 'repair', label: '+Repair' },
  { value: 'unpack', label: '+Unpack' },
  { value: 'full', label: '+Delete (full)' },
]

const label = <V extends string>(options: { value: V; label: string }[], value: V) => options.find((o) => o.value === value)?.label ?? value

const empty: DownloadCategoryInput = { name: '', folder: '', priority: 'default', postProcessing: 'full' }

/** SABnzbd's Categories page: folder, priority and post-processing per category. */
export function DownloadCategoriesSection() {
  const queryClient = useQueryClient()
  const categories = useQuery({ queryKey: downloadSettingsKeys.categories, queryFn: downloadSettingsApi.categories })
  const [editing, setEditing] = useState<number | 'new' | null>(null)
  const [draft, setDraft] = useState<DownloadCategoryInput>(empty)

  const refresh = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: downloadSettingsKeys.categories }),
      queryClient.invalidateQueries({ queryKey: downloadSettingsKeys.choices }),
    ])

  const save = useMutation({
    mutationFn: () => (editing === 'new' ? downloadSettingsApi.addCategory(draft) : downloadSettingsApi.saveCategory(editing as number, draft)),
    onSuccess: async () => {
      setEditing(null)
      await refresh()
    },
  })
  const remove = useMutation({ mutationFn: downloadSettingsApi.deleteCategory, onSuccess: refresh })

  const open = (category: DownloadCategory | null) => {
    save.reset()
    setDraft(category ? { name: category.name, folder: category.folder, priority: category.priority, postProcessing: category.postProcessing } : empty)
    setEditing(category ? category.id : 'new')
  }

  const fieldErrors = save.error instanceof ApiError ? save.error.fieldErrors : {}
  const formError = save.error && Object.keys(fieldErrors).length === 0 ? save.error.message : null
  const editingDefault = typeof editing === 'number' && categories.data?.find((c) => c.id === editing)?.isDefault

  const editor = (
    <div className={styles.editor}>
      <label>
        Name
        <input className={ui.input} value={draft.name} disabled={!!editingDefault} onChange={(e) => setDraft({ ...draft, name: e.target.value })} autoFocus={editing === 'new'} />
        {fieldErrors.name && <span className={styles.error}>{fieldErrors.name[0]}</span>}
      </label>
      <label>
        Folder
        <input
          className={`${ui.input} ${ui.mono}`}
          value={draft.folder}
          placeholder={draft.name || 'name'}
          spellCheck={false}
          onChange={(e) => setDraft({ ...draft, folder: e.target.value })}
        />
        {fieldErrors.folder && <span className={styles.error}>{fieldErrors.folder[0]}</span>}
      </label>
      <label>
        Priority
        <select className={ui.select} value={draft.priority} onChange={(e) => setDraft({ ...draft, priority: e.target.value as CategoryPriority })}>
          {categoryPriorities.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
        {fieldErrors.priority && <span className={styles.error}>{fieldErrors.priority[0]}</span>}
      </label>
      <label>
        Post-processing
        <select className={ui.select} value={draft.postProcessing} onChange={(e) => setDraft({ ...draft, postProcessing: e.target.value as PostProcessLevel })}>
          {postProcessLevels.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
      </label>
      <small className={`${styles.editorWide} ${styles.muted}`}>
        Folder: empty = the name below the completed folder, a relative path = below the completed folder, a full path = anywhere.
      </small>
      <div className={styles.editorActions}>
        <span className={styles.error}>{formError}</span>
        <button className={ui.button} type="button" onClick={() => setEditing(null)} disabled={save.isPending}>
          Cancel
        </button>
        <button className={ui.primary} type="button" onClick={() => save.mutate()} disabled={save.isPending || draft.name.trim() === ''}>
          {save.isPending ? 'Saving…' : editing === 'new' ? 'Add' : 'Save'}
        </button>
      </div>
    </div>
  )

  return (
    <Section
      id="categories"
      title="Categories"
      description="Each download has a category: it decides the folder, the priority and how much post-processing it gets. Grabs use movies and series; Add NZB and the Search page can pick any."
      footer={
        editing === null && (
          <button className={ui.button} type="button" onClick={() => open(null)}>
            Add category
          </button>
        )
      }
    >
      {categories.data && (
        <table className={styles.table}>
          <thead>
            <tr>
              <th>Name</th>
              <th>Folder</th>
              <th>Priority</th>
              <th>Post-processing</th>
              <th aria-label="Actions" />
            </tr>
          </thead>
          <tbody>
            {categories.data.map((category) => (
              <tr key={category.id}>
                <td className={styles.name}>
                  {category.name}
                  {category.paused && <span className={styles.badge}>paused</span>}
                </td>
                <td className={`${styles.path} ${styles.cellWide}`} title={category.path}>
                  {category.path}
                </td>
                <td>{label(categoryPriorities, category.priority)}</td>
                <td>{label(postProcessLevels, category.postProcessing)}</td>
                <td>
                  <div className={styles.actions}>
                    <button className={`${ui.button} ${styles.small}`} type="button" onClick={() => open(category)}>
                      Edit
                    </button>
                    {!category.isDefault && (
                      <button
                        className={`${ui.button} ${styles.small}`}
                        type="button"
                        disabled={remove.isPending}
                        onClick={() => {
                          if (window.confirm(`Delete the category ${category.name}? Downloads in it keep their folder.`)) remove.mutate(category.id)
                        }}
                      >
                        Delete
                      </button>
                    )}
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {remove.error && <span className={styles.error}>{remove.error.message}</span>}
      {editing !== null && editor}
    </Section>
  )
}
