import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { customFormatKeys, customFormatsApi, type CustomFormat, type FormatMediaType } from '../api/customFormats'
import { profileKeys } from '../api/profiles'
import { useAuth } from '../auth/auth'
import Icon from '../components/Icon'
import { Skeleton } from '../components/Loader'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import { SettingsPage } from '../settings/SettingsKit'
import styles from './CustomFormats.module.css'
import { summarize } from './describe'
import FormatEditor from './FormatEditor'
import { ExportDialog, ImportDialog } from './FormatJsonDialogs'
import FormatTestPanel from './FormatTestPanel'

type Filter = 'all' | FormatMediaType

const mediaLabels: Record<FormatMediaType, string> = { movie: 'Movies', series: 'Series' }

/** Settings → Custom formats: conditions on a release that quality profiles score (Radarr/Sonarr custom formats). */
export default function CustomFormatsPage() {
  const { user } = useAuth()
  const canEdit = user?.role === 'Owner' || user?.role === 'Admin'
  const formats = useQuery({ queryKey: customFormatKeys.all, queryFn: customFormatsApi.list })
  const movieSchema = useQuery({ queryKey: customFormatKeys.schema('movie'), queryFn: () => customFormatsApi.schema('movie'), staleTime: Infinity })
  const seriesSchema = useQuery({ queryKey: customFormatKeys.schema('series'), queryFn: () => customFormatsApi.schema('series'), staleTime: Infinity })
  const [filter, setFilter] = useState<Filter>('all')
  const [search, setSearch] = useState('')
  const [editing, setEditing] = useState<CustomFormat | null>(null)
  const [importing, setImporting] = useState(false)
  const [exporting, setExporting] = useState<CustomFormat | null>(null)
  const [deleting, setDeleting] = useState<CustomFormat | null>(null)

  const all = useMemo(() => formats.data ?? [], [formats.data])
  const counts = { movie: all.filter((f) => f.mediaType === 'movie').length, series: all.filter((f) => f.mediaType === 'series').length }

  const shown = useMemo(() => {
    const words = search.toLowerCase().split(/\s+/).filter(Boolean)
    return all
      .filter((f) => filter === 'all' || f.mediaType === filter)
      .filter((f) => {
        if (words.length === 0) return true
        const text = `${f.name} ${f.specifications.map((s) => `${s.name} ${s.fields.value ?? ''}`).join(' ')}`.toLowerCase()
        return words.every((w) => text.includes(w))
      })
      .sort((a, b) => a.name.localeCompare(b.name))
  }, [all, filter, search])

  const newFormat = () =>
    setEditing({ id: 0, name: '', mediaType: filter === 'series' ? 'series' : 'movie', includeInRename: false, specifications: [], trashId: null, usedBy: 0 })

  const groups = (['movie', 'series'] as const).map((type) => ({ type, items: shown.filter((f) => f.mediaType === type) })).filter((g) => g.items.length > 0)

  return (
    <SettingsPage
      title="Custom formats"
      lead="Conditions on the release name, group, language, source or size. Profiles give each format a score, and the best-scored release wins."
      actions={
        canEdit && (
          <>
            <button className={`${ui.button} ${ui.sm}`} type="button" onClick={() => setImporting(true)}>
              <Icon name="upload" size={14} />
              Import
            </button>
            <button className={`${ui.primary} ${ui.sm}`} type="button" onClick={newFormat}>
              <Icon name="plus" size={14} />
              New format
            </button>
          </>
        )
      }
    >
      <FormatTestPanel />

      <section className={ui.panel}>
        <div className={styles.toolbar}>
          <div className={ui.seg} role="group" aria-label="Show">
            <button type="button" aria-pressed={filter === 'all'} onClick={() => setFilter('all')}>
              All <span className={styles.segCount}>{all.length}</span>
            </button>
            <button type="button" aria-pressed={filter === 'movie'} onClick={() => setFilter('movie')}>
              Movies <span className={styles.segCount}>{counts.movie}</span>
            </button>
            <button type="button" aria-pressed={filter === 'series'} onClick={() => setFilter('series')}>
              Series <span className={styles.segCount}>{counts.series}</span>
            </button>
          </div>
          <label className={styles.search}>
            <Icon name="search" size={14} />
            <input
              className={ui.input}
              type="search"
              placeholder="Find a format"
              aria-label="Find a format"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
            />
          </label>
        </div>

        {formats.isPending && <Skeleton rows={5} />}
        {formats.isError && <p className={ui.errBox}>{formats.error.message}</p>}

        {formats.data && all.length === 0 && (
          <div className={ui.empty}>
            <span className={styles.emptyIcon}>
              <Icon name="tag" size={20} />
            </span>
            <h3>No custom formats yet</h3>
            <p className={styles.emptyText}>
              Create one, import JSON from Radarr, Sonarr or the TRaSH Guides, or let a TRaSH template under Settings → Profiles add the formats it
              needs.
            </p>
          </div>
        )}
        {formats.data && all.length > 0 && shown.length === 0 && <p className={styles.none}>No format matches “{search}”.</p>}

        {groups.map((g) => (
          <div key={g.type} className={styles.group}>
            {filter === 'all' && (
              <h3 className={styles.groupTitle}>
                <Icon name={g.type === 'movie' ? 'film' : 'tv'} size={14} />
                {mediaLabels[g.type]}
                <span className={styles.groupCount}>{g.items.length}</span>
              </h3>
            )}
            <ul className={styles.grid}>
              {g.items.map((f) => (
                <FormatCard
                  key={f.id}
                  format={f}
                  summary={summarize(f.specifications, f.mediaType === 'movie' ? movieSchema.data : seriesSchema.data)}
                  canEdit={canEdit}
                  onOpen={() => setEditing(f)}
                  onExport={() => setExporting(f)}
                  onDelete={() => setDeleting(f)}
                />
              ))}
            </ul>
          </div>
        ))}
      </section>

      {editing && <FormatEditor format={editing} canEdit={canEdit} onClose={() => setEditing(null)} onDelete={() => setDeleting(editing)} />}
      {importing && <ImportDialog defaultType={filter === 'series' ? 'series' : 'movie'} onClose={() => setImporting(false)} />}
      {exporting && <ExportDialog format={exporting} onClose={() => setExporting(null)} />}
      {deleting && (
        <DeleteDialog
          format={deleting}
          onClose={() => setDeleting(null)}
          onDeleted={() => {
            setDeleting(null)
            setEditing(null)
          }}
        />
      )}
    </SettingsPage>
  )
}

function FormatCard({
  format,
  summary,
  canEdit,
  onOpen,
  onExport,
  onDelete,
}: {
  format: CustomFormat
  summary: string[]
  canEdit: boolean
  onOpen: () => void
  onExport: () => void
  onDelete: () => void
}) {
  return (
    <li className={styles.card}>
      <button type="button" className={styles.cardMain} onClick={onOpen}>
        <span className={styles.cardHead}>
          <span className={styles.cardName}>{format.name}</span>
          {format.trashId && (
            <span className={styles.trash} title="From the TRaSH Guides">
              TRaSH
            </span>
          )}
        </span>
        <span className={styles.chips}>
          {summary.slice(0, 3).map((s) => (
            <span key={s} className={styles.chip}>
              {s}
            </span>
          ))}
          {summary.length > 3 && <span className={`${styles.chip} ${styles.chipMore}`}>+{summary.length - 3}</span>}
          {summary.length === 0 && <span className={ui.muted}>No conditions</span>}
        </span>
      </button>
      <div className={styles.cardFoot}>
        <span className={format.usedBy > 0 ? styles.used : styles.unused}>
          {format.usedBy > 0 ? `Scored in ${format.usedBy} profile${format.usedBy === 1 ? '' : 's'}` : 'Not scored'}
        </span>
        {format.includeInRename && (
          <span className={styles.rename} title="Shown in the {Custom Formats} naming token">
            <Icon name="rename" size={12} />
          </span>
        )}
        <span className={styles.cardActions}>
          <button type="button" className={`${ui.ghost} ${ui.sm} ${ui.icon}`} onClick={onExport} title="Export JSON" aria-label={`Export ${format.name}`}>
            <Icon name="code" size={14} />
          </button>
          {canEdit && (
            <button type="button" className={`${ui.ghost} ${ui.sm} ${ui.icon}`} onClick={onDelete} title="Delete" aria-label={`Delete ${format.name}`}>
              <Icon name="trash" size={14} />
            </button>
          )}
        </span>
      </div>
    </li>
  )
}

function DeleteDialog({ format, onClose, onDeleted }: { format: CustomFormat; onClose: () => void; onDeleted: () => void }) {
  const queryClient = useQueryClient()
  const remove = useMutation({
    mutationFn: () => customFormatsApi.remove(format.id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: customFormatKeys.all })
      await queryClient.invalidateQueries({ queryKey: profileKeys.profiles })
      onDeleted()
    },
  })
  return (
    <Modal title={`Delete ${format.name}?`} onClose={onClose}>
      <p className={styles.dialogText}>
        {format.usedBy > 0
          ? `It is scored in ${format.usedBy} profile${format.usedBy === 1 ? '' : 's'}; those scores are removed too.`
          : 'No profile gives it a score.'}
        {format.trashId && ' It comes from the TRaSH Guides: a template that needs it adds it again on the next update.'}
      </p>
      {remove.error && <p className={ui.errBox}>{remove.error.message}</p>}
      <div className={styles.dialogFoot}>
        <button className={ui.button} type="button" onClick={onClose}>
          Keep
        </button>
        <button className={ui.dangerSolid} type="button" onClick={() => remove.mutate()} disabled={remove.isPending}>
          <Icon name="trash" size={15} />
          {remove.isPending ? 'Deleting…' : 'Delete'}
        </button>
      </div>
    </Modal>
  )
}
