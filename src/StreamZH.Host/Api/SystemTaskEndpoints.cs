// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Host.Auth;
using StreamZH.Host.Jobs;

namespace StreamZH.Host.Api;

/// <summary>System → Tasks: the scheduled tasks with last and next run, and "Run now".</summary>
public static class SystemTaskEndpoints
{
    public static IEndpointRouteBuilder MapSystemTaskEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/system/tasks").WithTags("System").RequireAuthorization(AuthSchemes.AdminPolicy);

        group.MapGet("/", (ScheduledTaskRunner runner) => runner.States().Select(ScheduledTaskResource.From));

        group.MapPost("/{key}/run", (string key, ScheduledTaskRunner runner) =>
            runner.States().Any(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase))
                ? Results.Ok(new { queued = runner.RunNow(key) })
                : Results.NotFound());

        return app;
    }
}

/// <param name="IntervalMinutes">Null when the task is switched off.</param>
public sealed record ScheduledTaskResource(
    string Key,
    string Name,
    double? IntervalMinutes,
    DateTimeOffset? LastRun,
    DateTimeOffset? NextRun,
    double? LastDurationSeconds,
    bool? LastSuccess,
    string? LastError,
    bool Running,
    bool Queued)
{
    public static ScheduledTaskResource From(StreamZH.Core.Jobs.ScheduledTaskState s) => new(
        s.Key, s.Name, s.Interval?.TotalMinutes, s.LastRun, s.NextRun, s.LastDuration?.TotalSeconds, s.LastSuccess, s.LastError, s.Running, s.Queued);
}
