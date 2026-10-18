import { implementationLabel, type FormatSchema, type FormatSpecification } from '../api/customFormats'

/** Radarr/Sonarr use .NET regular expressions (case-insensitive). Close enough in the browser for a live check. */
export function browserRegex(pattern: string): RegExp | null {
  let source = pattern
  let flags = 'i'
  // Leading inline options like (?i) or (?x) are .NET only.
  const inline = /^\(\?([imsx]+)\)/.exec(source)
  if (inline) {
    source = source.slice(inline[0].length)
    if (inline[1].includes('s')) flags += 's'
    if (inline[1].includes('m')) flags += 'm'
  }
  try {
    return new RegExp(source, flags)
  } catch {
    return null
  }
}

/** The matched part of `text`, or null. */
export function regexMatch(pattern: string, text: string): { index: number; length: number } | null {
  const re = browserRegex(pattern)
  if (!re) return null
  const m = re.exec(text)
  return m ? { index: m.index, length: m[0].length } : null
}

function optionLabel(schema: FormatSchema | undefined, value: unknown): string | null {
  const field = schema?.fields.find((f) => f.name === 'value')
  const option = field?.options?.find((o) => o.value === Number(value))
  return option?.label ?? null
}

function flagLabels(schema: FormatSchema | undefined, value: number): string {
  const field = schema?.fields.find((f) => f.name === 'value')
  const names = (field?.options ?? []).filter((o) => o.value !== 0 && (value & o.value) === o.value).map((o) => o.label)
  return names.join(' + ') || String(value)
}

/** "German", "not Original", "1080p", "4–40 GB", "/\b(DL)\b/". */
export function describeSpec(spec: FormatSpecification, schemas: FormatSchema[] | undefined): string {
  const schema = schemas?.find((s) => s.implementation === spec.implementation)
  const field = schema?.fields.find((f) => f.name === 'value')
  const unit = schema?.fields.find((f) => f.name === 'min' || f.name === 'max')?.unit
  const not = spec.negate ? 'not ' : ''
  const { value, min, max } = spec.fields
  if (min !== undefined || max !== undefined) {
    const range = min != null && max != null ? `${min}–${max}` : min != null ? `≥ ${min}` : `≤ ${max}`
    return `${not}${range}${unit ? ` ${unit}` : ''}`
  }
  if (field?.type === 'flags') return not + flagLabels(schema, Number(value ?? 0))
  const label = optionLabel(schema, value)
  if (label) return not + label
  if (typeof value === 'string') return not + (value.length > 28 ? spec.name : `/${value}/`)
  return not + spec.name
}

/** Short summary chips for a format card: one per condition type, e.g. "Language: German, not Original", "Release group ×12". */
export function summarize(specs: FormatSpecification[], schemas: FormatSchema[] | undefined): string[] {
  const byType = new Map<string, FormatSpecification[]>()
  for (const spec of specs) byType.set(spec.implementation, [...(byType.get(spec.implementation) ?? []), spec])
  return [...byType.entries()].map(([implementation, list]) => {
    const label = implementationLabel(implementation)
    if (list.length > 2) return `${label} ×${list.length}`
    return `${label}: ${list.map((s) => describeSpec(s, schemas)).join(', ')}`
  })
}
