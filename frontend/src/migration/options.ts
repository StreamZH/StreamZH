// Options of a section, keyed "<section>.<option>" (request.options). The wizard renders them generically below the
// section's description; sections without an entry here show no options. The wizard always sends the value it shows.

/** What a default may depend on. */
export interface OptionContext {
  /** Side by side is on (already, or switched on for this take-over). */
  sideBySide: boolean
}

interface BaseOption {
  label: string
  help?: string
  advanced?: boolean
  default: string | ((context: OptionContext) => string)
}

export interface ChoiceOption extends BaseOption {
  kind: 'choice'
  /** Per source, when the wording differs ("As in Radarr"). */
  choices: { value: string; label: string }[] | ((source: string) => { value: string; label: string }[])
}

export interface NumberOption extends BaseOption {
  kind: 'number'
  unit: string
  min: number
  max?: number
}

/** A checkbox; the value is "true" or "false". */
export interface ToggleOption extends BaseOption {
  kind: 'toggle'
}

export type SectionOption = ChoiceOption | NumberOption | ToggleOption

const app = (source: string) => source.charAt(0).toUpperCase() + source.slice(1)

const monitor = (help: string): ChoiceOption => ({
  kind: 'choice',
  label: 'Monitor',
  help,
  choices: (source) => [
    { value: 'asIs', label: `As in ${app(source)}` },
    { value: 'none', label: 'None' },
  ],
  default: 'asIs',
})

const overwrite = (what: string): ToggleOption => ({
  kind: 'toggle',
  label: `Update ${what} already in StreamZH`,
  help: 'Takes their monitoring and quality profile over too; otherwise only their files are linked.',
  advanced: true,
  default: 'false',
})

export const sectionOptions: Record<string, SectionOption> = {
  'movies.monitor': monitor('Monitored movies are searched and upgraded once StreamZH grabs by itself.'),
  'movies.overwrite': overwrite('movies'),
  'series.monitor': monitor('Monitored episodes are searched and upgraded once StreamZH grabs by itself.'),
  'series.overwrite': overwrite('series'),
  'notifications.mediaBrowser': {
    kind: 'choice',
    label: 'Emby / Jellyfin connections go to',
    help: 'Radarr and Sonarr have one connection type for both.',
    choices: [
      { value: 'jellyfin', label: 'Jellyfin' },
      { value: 'emby', label: 'Emby' },
    ],
    default: 'jellyfin',
  },
  'history.days': {
    kind: 'number',
    label: 'Take over the last',
    help: '0 takes the whole history.',
    advanced: true,
    unit: 'days',
    min: 0,
    max: 3650,
    default: '90',
  },
  'servers.connections': {
    kind: 'choice',
    label: 'Connections',
    help: 'While SABnzbd still runs, half keeps both apps within your provider’s connection limit.',
    choices: [
      { value: 'half', label: 'Half of SABnzbd’s' },
      { value: 'same', label: 'The same as SABnzbd' },
    ],
    default: ({ sideBySide }) => (sideBySide ? 'half' : 'same'),
  },
}

/**
 * Connections whose old app masks more than one secret (Connect, by type name as the item's detail starts with it):
 * the first goes to secrets[itemKey], the others to secrets["itemKey.<field>"].
 */
export const secretFields: Record<string, { first: string; more: { field: string; label: string }[] }> = {
  pushover: { first: 'API key', more: [{ field: 'userKey', label: 'User key' }] },
  ntfy: { first: 'Access token', more: [{ field: 'password', label: 'Password' }] },
}

/** The options of one section: [full key, option]. */
export function optionsOf(section: string): [string, SectionOption][] {
  return Object.entries(sectionOptions).filter(([key]) => key.startsWith(`${section}.`))
}

export function choicesOf(option: ChoiceOption, source: string) {
  return typeof option.choices === 'function' ? option.choices(source) : option.choices
}

export function defaultOf(option: SectionOption, context: OptionContext): string {
  return typeof option.default === 'function' ? option.default(context) : option.default
}
