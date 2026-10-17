import type { ConnectKind, ConnectTrigger } from '../api/connect'

export const triggerLabels: Record<ConnectTrigger, string> = {
  grab: 'On Grab',
  import: 'On Import',
  upgrade: 'On Upgrade',
  rename: 'On Rename',
  downloadFailure: 'On Download Failure',
  healthIssue: 'On Health Issue',
  healthRestored: 'On Health Restored',
}

export const triggerHelp: Record<ConnectTrigger, string> = {
  grab: 'A release was sent to the downloader.',
  import: 'A new file was imported into the library.',
  upgrade: 'A better file replaced an existing one.',
  rename: 'Files were renamed in the library.',
  downloadFailure: 'A download failed (missing articles, repair or unpacking failed).',
  healthIssue: 'A warning or error appeared in the status window.',
  healthRestored: 'That warning or error went away again.',
}

export const kindLabels: Record<ConnectKind, string> = {
  mediaServer: 'Media servers',
  notification: 'Notifications',
}

export const kindHelp: Record<ConnectKind, string> = {
  mediaServer: 'Tell your media server about new and renamed files, so they show up at once.',
  notification: 'Get a message when something happens.',
}
