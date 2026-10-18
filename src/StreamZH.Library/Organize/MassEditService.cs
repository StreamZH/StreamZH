// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging;
using StreamZH.Core.Events;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library.Import;

namespace StreamZH.Library.Organize;

/// <summary>What the mass editor changes; null fields stay as they are.</summary>
/// <param name="MoveFiles">With a new root folder: move the folders there (as a job). False: only the library path changes.</param>
public sealed record MassEditChanges(bool? Monitored = null, int? QualityProfileId = null, int? RootFolderId = null, bool MoveFiles = false);

/// <param name="Updated">Items changed right away.</param>
/// <param name="ToMove">Items whose folder has to move to the new root folder (the caller queues the move).</param>
public sealed record MassEditResult(int Updated, IReadOnlyList<int> ToMove);

/// <summary>One removed item and what happened to its folder.</summary>
/// <param name="RecycledTo">Where the folder went in the recycle bin; null when it was deleted or kept.</param>
public sealed record RemovedItem(int Id, string Title, string Folder, bool FilesDeleted, string? RecycledTo);

public sealed record DeleteResult(IReadOnlyList<RemovedItem> Removed, IReadOnlyList<OrganizeError> Errors);

/// <summary>
/// The library mass editor (Radarr's and Sonarr's Mass Editor): monitored, quality profile and root folder for many
/// movies or series at once, moving folders to another root folder, and removing items from the library, with their
/// files only when asked (into the recycle bin of Media Management when one is set).
/// </summary>
public sealed class MassEditService(
    MovieRepository movies,
    SeriesRepository series,
    RootFolderRepository rootFolders,
    QualityProfileService profiles,
    MediaFileDetailsRepository details,
    MediaManagementSettings settings,
    ItemRelocator relocator,
    FolderMover mover,
    HistoryRepository history,
    LibraryRunTracker tracker,
    IEventBus events,
    ILogger<MassEditService> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<MassEditResult> EditAsync(MediaType mediaType, IReadOnlyCollection<int> ids, MassEditChanges changes)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(changes);
        var profileId = changes.QualityProfileId is { } requested ? profiles.Resolve(requested) : (int?)null;
        var root = changes.RootFolderId is { } rootId ? RootFolder(rootId, mediaType) : null;
        var updated = 0;
        var toMove = new List<int>();

        if (mediaType == MediaType.Movie)
        {
            foreach (var movie in movies.All().Where(m => ids.Contains(m.Id)))
            {
                var changed = Apply(movie, changes.Monitored, profileId);
                if (root is not null && (movie.RootFolderId != root.Id || !InRoot(movie.FolderPath, root)))
                {
                    if (changes.MoveFiles)
                    {
                        toMove.Add(movie.Id);
                    }
                    else
                    {
                        movie.FolderPath = Path.Combine(root.Path, Path.GetFileName(Path.TrimEndingDirectorySeparator(movie.FolderPath)));
                        movies.SetRootFolder(movie.Id, root.Id);
                        changed = true;
                    }
                }

                if (changed)
                {
                    movies.Update(movie);
                    updated++;
                }
            }
        }
        else if (mediaType == MediaType.Series)
        {
            foreach (var show in series.All().Where(s => ids.Contains(s.Id)))
            {
                var changed = Apply(show, changes.Monitored, profileId);
                if (root is not null && (show.RootFolderId != root.Id || !InRoot(show.FolderPath, root)))
                {
                    if (changes.MoveFiles)
                    {
                        toMove.Add(show.Id);
                    }
                    else
                    {
                        show.FolderPath = Path.Combine(root.Path, Path.GetFileName(Path.TrimEndingDirectorySeparator(show.FolderPath)));
                        series.SetRootFolder(show.Id, root.Id);
                        changed = true;
                    }
                }

                if (changed)
                {
                    series.Update(show);
                    updated++;
                }
            }
        }
        else
        {
            throw new LibraryException("The mass editor works for movies and series.", LibraryError.Invalid);
        }

        if (updated > 0)
        {
            logger.LogInformation("Mass editor: {Count} {Type} item(s) changed", updated, mediaType);
            await events.PublishAsync(new LibraryChanged(mediaType, ids.Count == 1 ? ids.First() : null));
        }

        return new MassEditResult(updated, toMove);
    }

    /// <summary>Throws when the root folder does not exist or is for another media type.</summary>
    public RootFolder RootFolder(int id, MediaType mediaType)
    {
        var root = rootFolders.Find(id) ?? throw new LibraryException("Root folder not found.", LibraryError.Invalid);
        return root.MediaType == mediaType
            ? root
            : throw new LibraryException($"This root folder is for {root.MediaType}, not {mediaType}.", LibraryError.Invalid);
    }

    /// <summary>
    /// Moves the folders into the root folder (Radarr's "Move files"), keeping their names; another drive means copy
    /// first, delete after. Each item that fails stays where it is, with the reason in the result.
    /// </summary>
    public async Task<LibraryRunResult> MoveAsync(MediaType mediaType, IReadOnlyCollection<int> ids, int rootFolderId, CancellationToken cancellationToken = default)
    {
        var root = RootFolder(rootFolderId, mediaType);
        var items = mediaType == MediaType.Movie
            ? movies.All().Where(m => ids.Contains(m.Id)).Select(m => (m.Id, m.Title, m.FolderPath)).ToList()
            : series.All().Where(s => ids.Contains(s.Id)).Select(s => (s.Id, s.Title, s.FolderPath)).ToList();
        tracker.Start(LibraryRunKind.Move, mediaType, OrganizeService.RunTitle(LibraryRunKind.Move, mediaType, items.Count), items.Count);
        int moved = 0, failed = 0;
        var errors = new List<OrganizeError>();
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (id, title, folder) = items[i];
                tracker.Report(i, title);
                var target = Path.Combine(root.Path, Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)));
                try
                {
                    var index = i;
                    relocator.Relocate(mediaType, id, folder, target, [], root.Id, f => tracker.Report(index, title, f));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    errors.Add(new OrganizeError(id, title, ex.Message));
                    logger.LogWarning("Could not move '{Title}' to {Root}: {Message}", title, root.Path, ex.Message);
                    continue;
                }

                moved++;
                logger.LogInformation("Moved '{Title}' from {Old} to {New}", title, folder, target);
                if (!PathComparer.Instance.Equals(folder, target))
                {
                    history.Add(new HistoryItem
                    {
                        Date = _time.GetUtcNow().UtcDateTime,
                        EventType = HistoryEventType.Renamed,
                        MediaType = mediaType,
                        MovieId = mediaType == MediaType.Movie ? id : null,
                        SeriesId = mediaType == MediaType.Series ? id : null,
                        SourceTitle = Path.GetFileName(folder),
                        Data = new Dictionary<string, string?> { ["from"] = folder, ["to"] = target, ["move"] = "rootFolder" },
                    });
                    await events.PublishAsync(
                        new MediaRenamed(mediaType, mediaType == MediaType.Movie ? id : null, mediaType == MediaType.Series ? id : null, target), cancellationToken);
                }
            }
        }
        finally
        {
            tracker.Finish(new LibraryRunResult(LibraryRunKind.Move, mediaType, null, _time.GetUtcNow().UtcDateTime, moved, 0, 0, failed, errors));
        }

        await events.PublishAsync(new LibraryChanged(mediaType, items.Count == 1 ? items[0].Id : null), cancellationToken);
        return tracker.Last!;
    }

    /// <summary>
    /// Removes the items from the library. Files stay on disk unless <paramref name="deleteFiles"/>: then the whole folder
    /// goes to the recycle bin (or is deleted when none is set). An item whose folder cannot be removed stays in the library.
    /// </summary>
    public async Task<DeleteResult> DeleteAsync(MediaType mediaType, IReadOnlyCollection<int> ids, bool deleteFiles)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var options = settings.Current;
        var removed = new List<RemovedItem>();
        var errors = new List<OrganizeError>();
        var items = mediaType switch
        {
            MediaType.Movie => movies.All().Where(m => ids.Contains(m.Id))
                .Select(m => (m.Id, m.Title, m.FolderPath, Videos: (IReadOnlyList<string>)(m.File is { } f ? [f.RelativePath] : []))).ToList(),
            MediaType.Series => series.All().Where(s => ids.Contains(s.Id))
                .Select(s => (s.Id, s.Title, s.FolderPath, Videos: (IReadOnlyList<string>)[.. series.Episodes(s.Id).Where(e => e.File is not null).Select(e => e.File!.RelativePath).Distinct()]))
                .ToList(),
            _ => throw new LibraryException("Only movies and series can be removed here.", LibraryError.Invalid),
        };

        foreach (var (id, title, folder, videos) in items)
        {
            string? recycled = null;
            if (deleteFiles)
            {
                try
                {
                    if (rootFolders.All().Any(r => PathComparer.Instance.Equals(r.Path, folder)))
                    {
                        throw new LibraryException("The folder is a root folder; it is never deleted.", LibraryError.Conflict);
                    }

                    recycled = mover.RecycleFolder(folder, options);
                }
                catch (LibraryException ex)
                {
                    errors.Add(new OrganizeError(id, title, ex.Message));
                    logger.LogWarning("Could not delete the folder of '{Title}': {Message}", title, ex.Message);
                    continue;
                }

                foreach (var video in videos)
                {
                    details.Delete(Path.Combine(folder, video));
                }
            }

            if (mediaType == MediaType.Movie)
            {
                movies.Delete(id);
            }
            else
            {
                series.Delete(id);
            }

            removed.Add(new RemovedItem(id, title, folder, deleteFiles, recycled));
            logger.LogInformation(
                deleteFiles ? "Removed '{Title}' from the library and deleted {Folder}" : "Removed '{Title}' from the library (files kept in {Folder})", title, folder);
        }

        if (removed.Count > 0)
        {
            await events.PublishAsync(new LibraryChanged(mediaType, removed.Count == 1 ? removed[0].Id : null));
        }

        return new DeleteResult(removed, errors);
    }

    private static bool Apply(Movie movie, bool? monitored, int? profileId)
    {
        var changed = false;
        if (monitored is { } m && movie.Monitored != m)
        {
            movie.Monitored = m;
            changed = true;
        }

        if (profileId is { } p && movie.QualityProfileId != p)
        {
            movie.QualityProfileId = p;
            changed = true;
        }

        return changed;
    }

    private static bool Apply(Series show, bool? monitored, int? profileId)
    {
        var changed = false;
        if (monitored is { } m && show.Monitored != m)
        {
            show.Monitored = m;
            changed = true;
        }

        if (profileId is { } p && show.QualityProfileId != p)
        {
            show.QualityProfileId = p;
            changed = true;
        }

        return changed;
    }

    private static bool InRoot(string folder, RootFolder root) =>
        PathComparer.Instance.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)), root.Path);
}
