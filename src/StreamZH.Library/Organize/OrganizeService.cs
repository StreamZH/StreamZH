// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Events;
using StreamZH.Core.Media;
using StreamZH.Data;

namespace StreamZH.Library.Organize;

/// <summary>
/// "Organize" for many movies or series at once (Radarr's and Sonarr's Organize, plus the folder): renames each folder
/// to the folder format and its files to the file formats, item by item, with progress in the status window. Every run
/// is written to a rename log, so the last one can be undone for <see cref="UndoWindow"/>.
/// </summary>
public sealed class OrganizeService(
    OrganizePlanner planner,
    ItemRelocator relocator,
    OrganizeLogRepository log,
    HistoryRepository history,
    MovieRepository movies,
    SeriesRepository series,
    LibraryRunTracker tracker,
    IEventBus events,
    ILogger<OrganizeService> logger,
    TimeProvider? timeProvider = null)
{
    /// <summary>How long "Undo last organize" is offered after a run.</summary>
    public static readonly TimeSpan UndoWindow = TimeSpan.FromHours(24);

    /// <summary>Rename logs older than this are removed.</summary>
    public static readonly TimeSpan LogRetention = TimeSpan.FromDays(30);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public IReadOnlyList<OrganizeItemPreview> Preview(MediaType mediaType, IReadOnlyCollection<int> ids) => planner.Plan(mediaType, ids);

    public static string RunTitle(LibraryRunKind kind, MediaType mediaType, int count)
    {
        var noun = mediaType == MediaType.Movie ? (count == 1 ? "movie" : "movies") : "series";
        var number = count.ToString(CultureInfo.InvariantCulture);
        return kind switch
        {
            LibraryRunKind.Undo => "Undo organize",
            LibraryRunKind.Move => $"Move {number} {noun}",
            _ => $"Organize {number} {noun}",
        };
    }

    public async Task<LibraryRunResult> ApplyAsync(MediaType mediaType, IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default)
    {
        log.DeleteOlderThan(Now - LogRetention);
        var plans = planner.Plan(mediaType, ids);
        tracker.Start(LibraryRunKind.Organize, mediaType, RunTitle(LibraryRunKind.Organize, mediaType, plans.Count), plans.Count);
        var runId = log.StartRun(mediaType, Now);
        int renamed = 0, unchanged = 0, skipped = 0, failed = 0;
        var errors = new List<OrganizeError>();
        try
        {
            for (var i = 0; i < plans.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var plan = plans[i];
                tracker.Report(i, plan.Title);
                if (!plan.HasChanges)
                {
                    unchanged++;
                    continue;
                }

                if (plan.Conflicts.Count > 0)
                {
                    skipped++;
                    errors.Add(new OrganizeError(plan.Id, plan.Title, $"Skipped: {plan.Conflicts[0]}"));
                    continue;
                }

                var index = i;
                var moves = plan.Files.Select(f => new FileMove(f.ExistingPath, f.NewPath, f.Kind == OrganizeFileKind.Video)).ToList();
                try
                {
                    relocator.Relocate(mediaType, plan.Id, plan.ExistingFolder, plan.NewFolder, moves, progress: f => tracker.Report(index, plan.Title, f));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    errors.Add(new OrganizeError(plan.Id, plan.Title, ex.Message));
                    logger.LogWarning("Could not organize '{Title}': {Message}", plan.Title, ex.Message);
                    continue;
                }

                log.AddItem(runId, new OrganizeItemLog(
                    plan.Id, plan.Title, plan.ExistingFolder, plan.NewFolder, [.. moves.Select(m => new OrganizeFileLog(m.From, m.To, m.IsVideo))]));
                AddHistory(mediaType, plan.Id, plan.ExistingFolder, plan.NewFolder, moves, undo: false);
                renamed++;
                logger.LogInformation(
                    "Organized '{Title}': {Old} → {New}, {Count} file(s) renamed", plan.Title, plan.ExistingFolder, plan.NewFolder, moves.Count);
                await PublishRenamedAsync(mediaType, plan.Id, plan.NewFolder, cancellationToken);
            }
        }
        finally
        {
            log.FinishRun(runId, Now, renamed, failed);
            tracker.Finish(new LibraryRunResult(
                LibraryRunKind.Organize, mediaType, renamed > 0 ? runId : null, Now, renamed, unchanged, skipped, failed, errors));
        }

        await events.PublishAsync(new LibraryChanged(mediaType, plans.Count == 1 ? plans[0].Id : null), cancellationToken);
        return tracker.Last!;
    }

    /// <summary>The last organize run, while it can still be undone (not undone yet, at most <see cref="UndoWindow"/> old).</summary>
    public OrganizeRun? Undoable()
    {
        var run = log.Last();
        return run is { UndoneAt: null, Finished: { } finished } && Now - finished <= UndoWindow ? run : null;
    }

    /// <summary>Renames everything of the run back, newest item first. Items changed since then are left alone.</summary>
    public async Task<LibraryRunResult> UndoAsync(int runId, CancellationToken cancellationToken = default)
    {
        var run = Undoable();
        if (run is null || run.Id != runId)
        {
            throw new LibraryException("This organize can no longer be undone.", LibraryError.Conflict);
        }

        var items = log.Items(runId).Reverse().ToList();
        tracker.Start(LibraryRunKind.Undo, run.MediaType, RunTitle(LibraryRunKind.Undo, run.MediaType, items.Count), items.Count);
        int restored = 0, failed = 0;
        var errors = new List<OrganizeError>();
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = items[i];
                tracker.Report(i, item.Title);
                var current = run.MediaType == MediaType.Movie ? movies.Find(item.ItemId)?.FolderPath : series.Find(item.ItemId)?.FolderPath;
                if (current is null)
                {
                    failed++;
                    errors.Add(new OrganizeError(item.ItemId, item.Title, "No longer in the library."));
                    continue;
                }

                if (!PathComparer.Instance.Equals(current, item.NewFolder))
                {
                    failed++;
                    errors.Add(new OrganizeError(item.ItemId, item.Title, "Moved or renamed again since then; left as it is."));
                    continue;
                }

                var moves = item.Files.Reverse().Select(f => new FileMove(f.NewPath, f.OldPath, f.IsVideo)).ToList();
                try
                {
                    var index = i;
                    relocator.Relocate(run.MediaType, item.ItemId, item.NewFolder, item.OldFolder, moves, progress: f => tracker.Report(index, item.Title, f));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    errors.Add(new OrganizeError(item.ItemId, item.Title, ex.Message));
                    logger.LogWarning("Could not undo the organize of '{Title}': {Message}", item.Title, ex.Message);
                    continue;
                }

                AddHistory(run.MediaType, item.ItemId, item.NewFolder, item.OldFolder, moves, undo: true);
                restored++;
                await PublishRenamedAsync(run.MediaType, item.ItemId, item.OldFolder, cancellationToken);
            }
        }
        finally
        {
            log.MarkUndone(runId, Now);
            tracker.Finish(new LibraryRunResult(LibraryRunKind.Undo, run.MediaType, null, Now, restored, 0, 0, failed, errors));
        }

        logger.LogInformation("Undid organize run {Run}: {Restored} item(s) restored, {Failed} failed", runId, restored, failed);
        await events.PublishAsync(new LibraryChanged(run.MediaType, items.Count == 1 ? items[0].ItemId : null), cancellationToken);
        return tracker.Last!;
    }

    private void AddHistory(MediaType mediaType, int id, string fromFolder, string toFolder, IReadOnlyList<FileMove> moves, bool undo)
    {
        var folderChanged = !string.Equals(fromFolder, toFolder, StringComparison.Ordinal);
        var video = moves.FirstOrDefault(m => m.IsVideo);
        var data = new Dictionary<string, string?>
        {
            ["from"] = folderChanged ? fromFolder : video?.From,
            ["to"] = folderChanged ? toFolder : video?.To,
            ["files"] = moves.Count.ToString(CultureInfo.InvariantCulture),
            ["organize"] = undo ? "undo" : "organize",
        };
        if (folderChanged && video is not null)
        {
            data["fileFrom"] = video.From;
            data["fileTo"] = video.To;
        }

        history.Add(new HistoryItem
        {
            Date = Now,
            EventType = HistoryEventType.Renamed,
            MediaType = mediaType,
            MovieId = mediaType == MediaType.Movie ? id : null,
            SeriesId = mediaType == MediaType.Series ? id : null,
            SourceTitle = folderChanged ? Path.GetFileName(fromFolder) : Path.GetFileName(video?.From ?? fromFolder),
            Data = data,
        });
    }

    private ValueTask PublishRenamedAsync(MediaType mediaType, int id, string folder, CancellationToken cancellationToken) =>
        events.PublishAsync(
            new MediaRenamed(mediaType, mediaType == MediaType.Movie ? id : null, mediaType == MediaType.Series ? id : null, folder), cancellationToken);
}
