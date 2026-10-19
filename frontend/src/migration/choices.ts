import type { MigrationAction, MigrationCounts, MigrationItem, MigrationPreview, MigrationRequest } from '../api/migration'
import { defaultOf, optionsOf, type OptionContext } from './options'

/** What the user picked in the preview: parts, items left out, typed-in passwords and section options. */
export interface Choices {
  selected: Set<string>
  excluded: Set<string>
  secrets: Record<string, string>
  options: Record<string, string>
}

export function initialChoices(preview: MigrationPreview): Choices {
  return {
    selected: new Set(preview.sections.filter((s) => s.defaultSelected).map((s) => s.key)),
    excluded: new Set(),
    secrets: {},
    options: {},
  }
}

/** Keeps the picks of an earlier preview (same source) after the preview was read again, e.g. with new path mappings. */
export function carryChoices(previous: Choices, before: MigrationPreview, preview: MigrationPreview): Choices {
  const keys = new Set(preview.sections.map((s) => s.key))
  const items = new Set(preview.sections.flatMap((s) => s.items.map((i) => i.key)))
  const selected = new Set([...previous.selected].filter((k) => keys.has(k)))
  // A part that could not be read before (no items, not picked by default) and can now is picked by its default.
  for (const s of preview.sections) {
    const old = before.sections.find((b) => b.key === s.key)
    if (s.defaultSelected && (!old || !old.defaultSelected)) selected.add(s.key)
  }
  return {
    selected,
    excluded: new Set([...previous.excluded].filter((k) => items.has(k))),
    secrets: Object.fromEntries(Object.entries(previous.secrets).filter(([k]) => items.has(k))),
    options: previous.options,
  }
}

export const actionLabels: Record<MigrationAction, string> = { create: 'New', update: 'Update', link: 'Link', skip: 'Skip' }

/** Counts of what will happen, with the items left out counted apart. */
export function countItems(items: MigrationItem[], excluded: Set<string>): MigrationCounts & { out: number } {
  const counts = { create: 0, update: 0, link: 0, skip: 0, out: 0 }
  for (const item of items) {
    if (item.action !== 'skip' && excluded.has(item.key)) counts.out++
    else counts[item.action]++
  }
  return counts
}

/** The request body for a run (or dry run) from the picks; only parts that are visible (Advanced) count. */
export function buildRequest(
  base: Pick<MigrationRequest, 'url' | 'apiKey' | 'pathMappings'>,
  preview: MigrationPreview,
  choices: Choices,
  advanced: boolean,
  dryRun: boolean,
  context: OptionContext,
): MigrationRequest {
  const sections = preview.sections.filter((s) => choices.selected.has(s.key) && (advanced || !s.advanced))
  const itemKeys = new Set(sections.flatMap((s) => s.items.map((i) => i.key)))
  const options: Record<string, string> = {}
  for (const section of sections) {
    for (const [key, option] of optionsOf(section.key)) {
      if (option.advanced && !advanced) continue
      options[key] = choices.options[key] ?? defaultOf(option, context)
    }
  }
  return {
    ...base,
    sections: sections.map((s) => s.key),
    exclude: [...choices.excluded].filter((k) => itemKeys.has(k)),
    // Further secrets of an item are keyed "<item key>.<field>" (item keys may contain dots themselves).
    secrets: Object.fromEntries(
      Object.entries(choices.secrets).filter(([k, v]) => {
        const item = itemKeys.has(k) ? k : k.slice(0, k.lastIndexOf('.'))
        return !!v && itemKeys.has(item) && !choices.excluded.has(item)
      }),
    ),
    options,
    dryRun,
  }
}

export const plural = (n: number, one: string, many = `${one}s`) => `${n.toLocaleString()} ${n === 1 ? one : many}`
