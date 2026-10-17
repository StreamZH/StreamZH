// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Jobs;
using StreamZH.Core.Media;
using StreamZH.Data;

namespace StreamZH.Library.Organize;

/// <summary>Organize, undo and move run as background jobs (one at a time), with their progress in the status window.</summary>
public static class OrganizeJobs
{
    /// <summary>Queues the run; false when another organize, undo or move is queued or running.</summary>
    public static bool TryQueue(
        JobQueue jobs, LibraryRunTracker tracker, LibraryRunKind kind, MediaType mediaType, int count, Func<IServiceProvider, CancellationToken, Task> run)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(tracker);
        var title = OrganizeService.RunTitle(kind, mediaType, count);
        if (!tracker.TryQueue(kind, mediaType, title, count))
        {
            return false;
        }

        var job = new Job($"library-run:{Guid.NewGuid():N}", title, async (sp, ct) =>
        {
            try
            {
                await run(sp, ct);
            }
            catch (Exception ex)
            {
                // The run never started (or broke outside an item): free the slot and show why.
                if (tracker.Current is not null)
                {
                    tracker.Finish(new LibraryRunResult(kind, mediaType, null, DateTime.UtcNow, 0, 0, 0, count, [new OrganizeError(0, title, ex.Message)]));
                }

                throw;
            }
        });

        if (!jobs.Enqueue(job))
        {
            tracker.Release();
            return false;
        }

        return true;
    }

    public static bool Organize(JobQueue jobs, LibraryRunTracker tracker, MediaType mediaType, IReadOnlyList<int> ids) =>
        TryQueue(jobs, tracker, LibraryRunKind.Organize, mediaType, ids.Count, (sp, ct) => sp.GetRequiredService<OrganizeService>().ApplyAsync(mediaType, ids, ct));

    public static bool Undo(JobQueue jobs, LibraryRunTracker tracker, OrganizeRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return TryQueue(jobs, tracker, LibraryRunKind.Undo, run.MediaType, run.Renamed, (sp, ct) => sp.GetRequiredService<OrganizeService>().UndoAsync(run.Id, ct));
    }

    public static bool Move(JobQueue jobs, LibraryRunTracker tracker, MediaType mediaType, IReadOnlyList<int> ids, int rootFolderId) =>
        TryQueue(jobs, tracker, LibraryRunKind.Move, mediaType, ids.Count, (sp, ct) => sp.GetRequiredService<MassEditService>().MoveAsync(mediaType, ids, rootFolderId, ct));
}

public static class OrganizeServiceCollectionExtensions
{
    /// <summary>The library mass editor, "Organize" with its rename log and undo, and moving items to another root folder.</summary>
    public static IServiceCollection AddLibraryOrganize(this IServiceCollection services)
    {
        services.AddSingleton<LibraryRunTracker>();
        services.AddSingleton<OrganizeLogRepository>();
        services.AddSingleton<FolderMover>();
        services.AddScoped<OrganizePlanner>();
        services.AddScoped<ItemRelocator>();
        services.AddScoped<OrganizeService>();
        services.AddScoped<MassEditService>();
        return services;
    }
}
