// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Core.Jobs;
using StreamZH.Data;
using StreamZH.Library.Search;

namespace StreamZH.Library.Import;

/// <summary>The background jobs of completed and failed download handling (they show in the status window).</summary>
public static class ImportJobs
{
    public static Job Import(int downloadId, string title, ManualImportRequest? manual = null) =>
        new($"download-import:{downloadId}", $"Import {title}", async (sp, ct) =>
            await sp.GetRequiredService<DownloadImportService>().ImportAsync(downloadId, manual, ct));

    public static Job Failed(DownloadItem item) =>
        new($"download-failed:{item.Id}", $"Failed download handling: {item.Title}", async (sp, ct) =>
        {
            var handler = sp.GetRequiredService<FailedDownloadHandler>();
            if (await handler.HandleAsync(item))
            {
                await FailedDownloadHandler.SearchAgainAsync(
                    item, sp.GetRequiredService<AutomaticSearchService>(), sp.GetRequiredService<SeriesRepository>(), ct);
            }
        });

    /// <summary>Queues the import and marks it as queued, so Activity shows it at once.</summary>
    public static void Enqueue(JobQueue jobs, DownloadImportService service, DownloadItem item, ManualImportRequest? manual = null)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(item);
        service.MarkQueued(item, manual);
        jobs.Enqueue(Import(item.Id, item.Title, manual));
    }
}

/// <summary>
/// Starts completed download handling (import) and failed download handling when the engine reports a finished download,
/// and picks up imports that were queued or running when StreamZH stopped.
/// </summary>
public sealed class ImportEventHandler(
    IEventBus events,
    JobQueue jobs,
    MediaManagementSettings settings,
    DownloadImportRepository imports,
    IServiceScopeFactory scopes,
    ILogger<ImportEventHandler> logger,
    IDownloadQueue? queue = null) : IHostedService
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = events.Subscribe<DownloadFinished>(OnFinishedAsync);
        foreach (var pending in imports.WithState(ImportState.Queued, ImportState.Importing))
        {
            if (queue?.Find(pending.DownloadId) is { Status: DownloadStatus.Completed })
            {
                logger.LogInformation("Resuming the import of download {Id} '{Title}'", pending.DownloadId, pending.Title);
                jobs.Enqueue(ImportJobs.Import(pending.DownloadId, pending.Title));
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    private ValueTask OnFinishedAsync(DownloadFinished finished, CancellationToken cancellationToken)
    {
        var item = finished.Item;
        if (item.Link.MediaType is null)
        {
            return ValueTask.CompletedTask;
        }

        if (item.Status == DownloadStatus.Completed)
        {
            if (!settings.Current.CompletedDownloadHandling)
            {
                logger.LogInformation("Completed download handling is off; download {Id} '{Title}' waits for a manual import", item.Id, item.Title);
                return ValueTask.CompletedTask;
            }

            using var scope = scopes.CreateScope();
            ImportJobs.Enqueue(jobs, scope.ServiceProvider.GetRequiredService<DownloadImportService>(), item);
        }
        else if (item.Status == DownloadStatus.Failed)
        {
            jobs.Enqueue(ImportJobs.Failed(item));
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>Tries the held-back imports again (episode title not known yet), every few minutes.</summary>
public sealed class PendingImportsTask(DownloadImportRepository imports) : IScheduledTask
{
    public string Key => "pending-imports";

    public string Name => "Check waiting imports";

    public TimeSpan? Interval => TimeSpan.FromMinutes(5);

    public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var service = services.GetRequiredService<DownloadImportService>();
        foreach (var waiting in imports.WithState(ImportState.Waiting))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await service.ImportAsync(waiting.DownloadId, null, cancellationToken);
            }
            catch (LibraryException)
            {
                // The download is gone or no longer completed: forget the waiting import.
                imports.Delete(waiting.DownloadId);
            }
        }
    }
}

/// <summary>Deletes files that have been in the recycle bin longer than the cleanup days (Radarr's "Recycling Bin Cleanup").</summary>
public sealed class RecycleBinCleanupTask(MediaManagementSettings settings, ImportFileSystem files, ILogger<RecycleBinCleanupTask> logger, TimeProvider? timeProvider = null)
    : IScheduledTask
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public string Key => "recycle-bin-cleanup";

    public string Name => "Clean up recycle bin";

    public TimeSpan? Interval =>
        settings.Current is { RecycleBin.Length: > 0, RecycleBinCleanupDays: > 0 } ? TimeSpan.FromHours(24) : null;

    public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        Clean();
        return Task.CompletedTask;
    }

    /// <summary>Returns the number of deleted files.</summary>
    public int Clean()
    {
        var options = settings.Current;
        if (options.RecycleBin.Length == 0 || options.RecycleBinCleanupDays <= 0 || !Directory.Exists(options.RecycleBin))
        {
            return 0;
        }

        var cutoff = _time.GetUtcNow().UtcDateTime.AddDays(-options.RecycleBinCleanupDays);
        var deleted = 0;
        var all = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var file in new DirectoryInfo(options.RecycleBin).EnumerateFiles("*", all))
        {
            if (file.LastWriteTimeUtc >= cutoff)
            {
                continue;
            }

            try
            {
                file.Delete();
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Could not delete {File} from the recycle bin: {Message}", file.FullName, ex.Message);
            }
        }

        foreach (var folder in Directory.EnumerateDirectories(options.RecycleBin))
        {
            files.DeleteEmptyFolders(folder, includeSelf: true);
        }

        logger.LogInformation("Recycle bin cleanup: {Count} file(s) older than {Days} days deleted", deleted, options.RecycleBinCleanupDays);
        return deleted;
    }
}

public static class ImportServiceCollectionExtensions
{
    /// <summary>Media Management: settings, naming, import (completed/failed download handling), blocklist, rename and their tasks.</summary>
    public static IServiceCollection AddMediaManagement(this IServiceCollection services)
    {
        services.AddSingleton<MediaManagementSettings>();
        services.AddSingleton<DownloadImportRepository>();
        services.AddSingleton<StreamZH.Library.Search.PendingImports>();
        services.AddSingleton<BlocklistRepository>();
        services.AddSingleton<MediaFileDetailsRepository>();
        services.AddSingleton<EpisodeFileRepository>();
        services.AddSingleton<ImportFileSystem>();
        services.AddScoped<DownloadImportService>();
        services.AddScoped<FailedDownloadHandler>();
        services.AddScoped<RenameService>();
        services.AddHostedService<ImportEventHandler>();
        services.AddSingleton<IScheduledTask, PendingImportsTask>();
        services.AddSingleton<IScheduledTask, RecycleBinCleanupTask>();
        return services;
    }
}
