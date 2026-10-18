import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useRef, useState } from 'react'
import { ApiError } from '../api/client'
import { customFormatKeys } from '../api/customFormats'
import { profileKeys } from '../api/profiles'
import { trashApi, trashKeys, type RecyclarrResult } from '../api/trash'
import Icon from '../components/Icon'
import Modal from '../components/Modal'
import ui from '../components/ui.module.css'
import styles from './Trash.module.css'

/** «Import recyclarr.yml»: paste the YAML, look at what it would do, then apply. */
export default function RecyclarrDialog({ onClose }: { onClose: () => void }) {
  const queryClient = useQueryClient()
  const [yaml, setYaml] = useState('')
  const [preview, setPreview] = useState<RecyclarrResult | null>(null)
  const file = useRef<HTMLInputElement>(null)

  const check = useMutation({ mutationFn: () => trashApi.recyclarr(yaml, false), onSuccess: setPreview })
  const apply = useMutation({
    mutationFn: () => trashApi.recyclarr(yaml, true),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: profileKeys.profiles })
      await queryClient.invalidateQueries({ queryKey: customFormatKeys.all })
      await queryClient.invalidateQueries({ queryKey: trashKeys.status })
    },
  })
  const error = check.error ?? apply.error
  const yamlError = error instanceof ApiError ? (error.fieldErrors.yaml?.[0] ?? null) : null
  const result = apply.data ?? preview

  return (
    <Modal title="Import recyclarr.yml" onClose={onClose}>
      <div className={styles.wizard}>
        {apply.isSuccess ? (
          <p className={ui.infoBox}>
            <Icon name="check" size={15} />
            {apply.data.profiles.length} profile{apply.data.profiles.length === 1 ? '' : 's'} created or updated. StreamZH keeps them in sync with the guides
            from now on, so Recyclarr is no longer needed.
          </p>
        ) : (
          <>
            <p className={styles.text}>
              Paste your Recyclarr configuration. StreamZH reads the quality profiles, templates, custom formats and score overrides of every Radarr and
              Sonarr instance in it. Nothing changes until you apply.
            </p>
            <div className={styles.yamlBar}>
              <button type="button" className={`${ui.button} ${ui.sm}`} onClick={() => file.current?.click()}>
                <Icon name="file" size={14} />
                Open file
              </button>
              <input
                ref={file}
                type="file"
                accept=".yml,.yaml,text/yaml"
                hidden
                onChange={async (e) => {
                  const f = e.target.files?.[0]
                  if (f) {
                    setYaml(await f.text())
                    setPreview(null)
                  }
                  e.target.value = ''
                }}
              />
            </div>
            <textarea
              className={`${ui.input} ${ui.mono} ${styles.yaml}`}
              value={yaml}
              onChange={(e) => {
                setYaml(e.target.value)
                setPreview(null)
              }}
              placeholder={'radarr:\n  movies:\n    base_url: http://localhost:7878\n    include:\n      - template: radarr-quality-profile-german-hd-bluray-web\n      - template: radarr-custom-formats-german-hd-bluray-web'}
              spellCheck={false}
              aria-label="recyclarr.yml"
              autoFocus
            />
          </>
        )}

        {(yamlError || (error && !yamlError)) && <p className={ui.errBox}>{yamlError ?? error?.message}</p>}

        {result && (
          <div className={styles.recyclarrResult}>
            <div className={styles.tableWrap}>
              <table className={ui.table}>
                <thead>
                  <tr>
                    <th>Profile</th>
                    <th>Template</th>
                    <th className={styles.numCol}>Formats</th>
                    <th className={styles.numCol}>Own scores</th>
                  </tr>
                </thead>
                <tbody>
                  {result.profiles.map((p) => (
                    <tr key={p.name}>
                      <td>
                        <strong>{p.name}</strong>
                      </td>
                      <td>{p.template ?? <span className={ui.muted}>none</span>}</td>
                      <td className={styles.numCol}>{p.formats}</td>
                      <td className={styles.numCol}>{p.overrides}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            {result.warnings.length > 0 && (
              <ul className={styles.warnings}>
                {result.warnings.map((w) => (
                  <li key={w}>
                    <Icon name="alert" size={14} />
                    {w}
                  </li>
                ))}
              </ul>
            )}
          </div>
        )}

        <div className={styles.foot}>
          <span className={styles.spacer} />
          <button className={ui.button} type="button" onClick={onClose}>
            {apply.isSuccess ? 'Close' : 'Cancel'}
          </button>
          {!apply.isSuccess &&
            (preview ? (
              <button className={ui.primary} type="button" onClick={() => apply.mutate()} disabled={apply.isPending || preview.profiles.length === 0}>
                <Icon name="check" size={15} />
                {apply.isPending ? 'Applying…' : `Apply ${preview.profiles.length} profile${preview.profiles.length === 1 ? '' : 's'}`}
              </button>
            ) : (
              <button className={ui.primary} type="button" onClick={() => check.mutate()} disabled={!yaml.trim() || check.isPending}>
                <Icon name="eye" size={15} />
                {check.isPending ? 'Reading…' : 'Preview'}
              </button>
            ))}
        </div>
      </div>
    </Modal>
  )
}
