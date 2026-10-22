import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'
import { ApiError } from '../api/client'
import { isLive, migrationApi, migrationKeys, type AutomationSettings, type MigrationRun } from '../api/migration'
import ui from '../components/ui.module.css'
import { useAdvanced } from '../lib/advancedMode'
import { useIsAdmin } from '../status/useOngoing'

/** GET /settings/automation (side by side) for admins; undefined while loading or when the backend does not have it. */
export function useAutomation() {
  const admin = useIsAdmin()
  return useQuery({
    queryKey: migrationKeys.automation,
    queryFn: migrationApi.automation,
    enabled: admin,
    retry: (count, error) => !(error instanceof ApiError && error.status === 404) && count < 1,
    refetchInterval: 60_000,
  })
}

/**
 * True while a migration is not finished: side by side is on (StreamZH grabs nothing until the owner switches over) or a
 * run is still going. Settings then shows Migration even without the Advanced switch. `enabled`: only ask when it matters.
 */
export function useMigrationUnfinished(enabled = true): boolean {
  const admin = useIsAdmin()
  const automation = useAutomation()
  const runs = useQuery({
    queryKey: migrationKeys.runs,
    queryFn: migrationApi.runs,
    enabled: admin && enabled,
    staleTime: 30_000,
    retry: (count, error) => !(error instanceof ApiError && error.status === 404) && count < 1,
  })
  return !!automation.data?.hold || !!runs.data?.some(isLive)
}

/**
 * Settings → Migration is for advanced use: it shows with the Advanced switch on, or by itself while a migration is not
 * finished (`unfinished`). Its address works either way.
 */
export function useShowMigration(enabled = true): { show: boolean; unfinished: boolean } {
  const advanced = useAdvanced()
  const unfinished = useMigrationUnfinished(enabled)
  return { show: advanced || unfinished, unfinished }
}

export function useSetAutomation() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: migrationApi.setAutomation,
    onSuccess: (data, hold) =>
      queryClient.setQueryData<AutomationSettings>(migrationKeys.automation, data ?? { hold, since: hold ? new Date().toISOString() : null }),
    onSettled: () => queryClient.invalidateQueries({ queryKey: migrationKeys.automation }),
  })
}

/** Polls a run every second while it runs; refreshes the run list once it is finished. */
export function useRun(id: number) {
  const queryClient = useQueryClient()
  const query = useQuery({
    queryKey: migrationKeys.run(id),
    queryFn: () => migrationApi.find(id),
    refetchInterval: (q) => (!q.state.data || isLive(q.state.data) ? 1000 : false),
  })
  const live = query.data ? isLive(query.data) : true
  useEffect(() => {
    if (!live) void queryClient.invalidateQueries({ queryKey: migrationKeys.runs })
  }, [live, queryClient])
  return query
}

export const statusPill: Record<MigrationRun['status'], [string, string]> = {
  queued: [ui.pillBusy, 'Waiting'],
  running: [ui.pillBusy, 'Running'],
  done: [ui.pillOk, 'Done'],
  failed: [ui.pillErr, 'Failed'],
  cancelled: [ui.pillWarn, 'Cancelled'],
}

export function totals(run: MigrationRun) {
  const t = { created: 0, updated: 0, linked: 0, skipped: 0, failed: 0, warnings: 0, errors: 0, infos: 0 }
  for (const s of run.report.sections) {
    t.created += s.created
    t.updated += s.updated
    t.linked += s.linked
    t.skipped += s.skipped
    t.failed += s.failed
    for (const m of s.messages) {
      if (m.level === 'warn') t.warnings++
      else if (m.level === 'error') t.errors++
      else t.infos++
    }
  }
  return t
}

/** "120 new · 4 updated · 980 linked · 2 failed" */
export function summaryLine(run: MigrationRun): string {
  const t = totals(run)
  const parts = [
    t.created && `${t.created.toLocaleString()} new`,
    t.updated && `${t.updated.toLocaleString()} updated`,
    t.linked && `${t.linked.toLocaleString()} linked`,
    t.skipped && `${t.skipped.toLocaleString()} skipped`,
    t.failed && `${t.failed.toLocaleString()} failed`,
  ].filter(Boolean)
  return parts.length ? parts.join(' · ') : 'Nothing changed'
}
