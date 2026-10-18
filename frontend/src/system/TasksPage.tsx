import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { generalApi, generalKeys, type ScheduledTask } from '../api/general'
import { libraryApi, queryKeys } from '../api/library'
import Icon from '../components/Icon'
import { Skeleton } from '../components/Loader'
import ui from '../components/ui.module.css'
import { formatAgo, formatDateTime } from '../lib/format'
import { Section } from '../settings/SettingsKit'
import styles from './System.module.css'
import { SystemPage } from './SystemPage'

function formatInterval(minutes: number | null): string {
  if (minutes == null) return 'Off'
  if (minutes % 1440 === 0) return minutes === 1440 ? 'Daily' : `Every ${minutes / 1440} days`
  if (minutes % 60 === 0) return minutes === 60 ? 'Hourly' : `Every ${minutes / 60} h`
  return `Every ${minutes} min`
}

function formatSeconds(seconds: number | null): string {
  if (seconds == null) return '–'
  if (seconds < 1) return '< 1 s'
  if (seconds < 60) return `${Math.round(seconds)} s`
  return `${Math.floor(seconds / 60)} min ${Math.round(seconds % 60)} s`
}

/** System → Tasks (like Radarr): the scheduled tasks with "Run now", and the background job queue. */
export default function TasksPage() {
  const tasks = useQuery({
    queryKey: generalKeys.tasks,
    queryFn: generalApi.tasks,
    refetchInterval: (q) => (q.state.data?.some((t) => t.running || t.queued) ? 3_000 : 30_000),
  })
  const jobs = useQuery({ queryKey: queryKeys.jobs, queryFn: libraryApi.jobs })

  return (
    <SystemPage title="Tasks" lead="Scheduled tasks run on their own; Run now starts one at once. All work goes through one queue, one job at a time.">
      <Section id="scheduled" title="Scheduled">
        {tasks.isError && <p className={ui.error}>{tasks.error.message}</p>}
        {tasks.isPending && <Skeleton rows={6} />}
        {tasks.data && tasks.data.length === 0 && <p className={styles.muted}>No scheduled tasks.</p>}
        {tasks.data && tasks.data.length > 0 && (
          <div className={`${styles.table} ${styles.tasks}`} role="table" aria-label="Scheduled tasks">
            <div className={styles.head} role="row">
              <span role="columnheader">Name</span>
              <span role="columnheader">Interval</span>
              <span role="columnheader">Last run</span>
              <span role="columnheader">Duration</span>
              <span role="columnheader">Next run</span>
              <span role="columnheader" />
            </div>
            {tasks.data.map((task) => (
              <TaskRow key={task.key} task={task} />
            ))}
          </div>
        )}
      </Section>

      <Section id="queue" title="Queue" description="Refreshes, searches, imports and scheduled tasks wait here. The status window shows the running one too.">
        {jobs.data && (
          <div className={styles.queue}>
            <div className={styles.queueItem}>
              <span className={ui.eyebrow}>Running</span>
              {jobs.data.running ? (
                <span className={styles.queueRunning}>
                  <span className={ui.dotBusy} />
                  {jobs.data.running}
                </span>
              ) : (
                <span className={`${styles.queueValue} ${styles.muted}`}>Idle</span>
              )}
            </div>
            <div className={styles.queueItem}>
              <span className={ui.eyebrow}>Waiting</span>
              <span className={styles.queueValue}>{jobs.data.queued}</span>
            </div>
            <div className={styles.queueItem}>
              <span className={ui.eyebrow}>Since start</span>
              <span className={styles.queueValue}>
                {jobs.data.completedSinceStart}
                <small>done</small>
                {jobs.data.failedSinceStart > 0 && <small className={styles.errorText}>· {jobs.data.failedSinceStart} failed</small>}
              </span>
            </div>
          </div>
        )}
      </Section>
    </SystemPage>
  )
}

function TaskRow({ task }: { task: ScheduledTask }) {
  const queryClient = useQueryClient()
  const run = useMutation({
    mutationFn: () => generalApi.runTask(task.key),
    onSettled: () => queryClient.invalidateQueries({ queryKey: generalKeys.tasks }),
  })
  const busy = task.running || task.queued || run.isPending

  return (
    <div className={`${styles.row} ${task.running ? styles.rowRunning : ''}`} role="row">
      <span role="cell" className={styles.cell}>
        <span className={styles.name}>{task.name}</span>
        {task.running && <span className={styles.indeterminate} aria-hidden="true" />}
        {task.lastSuccess === false && task.lastError && (
          <div className={`${styles.errorText} ${styles.small}`}>{task.lastError}</div>
        )}
      </span>
      <span role="cell" className={styles.cell}>
        <span className={styles.cellLabel}>Interval</span>
        {formatInterval(task.intervalMinutes)}
      </span>
      <span role="cell" className={styles.cell} title={task.lastRun ? formatDateTime(task.lastRun) : undefined}>
        <span className={styles.cellLabel}>Last run</span>
        {task.lastRun ? (
          <span className={styles.status}>
            <span className={task.lastSuccess === false ? ui.dotErr : task.lastSuccess ? ui.dotOk : ui.dot} />
            {formatAgo(task.lastRun)}
          </span>
        ) : (
          'Never'
        )}
      </span>
      <span role="cell" className={styles.cell}>
        <span className={styles.cellLabel}>Duration</span>
        {formatSeconds(task.lastDurationSeconds)}
      </span>
      <span role="cell" className={styles.cell} title={task.nextRun ? formatDateTime(task.nextRun) : undefined}>
        <span className={styles.cellLabel}>Next run</span>
        {task.running ? 'Running now' : task.queued ? 'Waiting in the queue' : task.nextRun ? formatAgo(task.nextRun) : '–'}
      </span>
      <span role="cell" className={`${styles.cell} ${styles.right}`}>
        <button className={`${ui.button} ${ui.sm} ${styles.runButton}`} type="button" onClick={() => run.mutate()} disabled={busy} aria-label={`Run ${task.name} now`}>
          {busy ? <span className={styles.spinner} /> : <Icon name="play" size={13} />}
          {task.running ? 'Running' : task.queued ? 'Queued' : 'Run now'}
        </button>
      </span>
    </div>
  )
}
