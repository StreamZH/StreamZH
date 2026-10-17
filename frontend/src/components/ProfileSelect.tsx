import { useProfileChoices } from '../api/profiles'
import ui from './ui.module.css'

/** Quality profile picker. A null value shows the default profile as selected. */
export default function ProfileSelect({
  value,
  onChange,
  disabled,
  className,
  label = 'Quality profile',
}: {
  value: number | null
  onChange: (id: number) => void
  disabled?: boolean
  className?: string
  label?: string
}) {
  const { profiles, defaultId } = useProfileChoices()
  const effective = value ?? defaultId ?? profiles[0]?.id ?? ''

  return (
    <select
      className={className ?? ui.select}
      value={effective}
      onChange={(e) => onChange(Number(e.target.value))}
      disabled={disabled || profiles.length === 0}
      aria-label={label}
    >
      {profiles.length === 0 && <option value="">Loading…</option>}
      {profiles.map((p) => (
        <option key={p.id} value={p.id}>
          {p.name}
          {p.id === defaultId ? ' (default)' : ''}
        </option>
      ))}
    </select>
  )
}
