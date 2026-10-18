// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library.Import;

namespace StreamZH.Library.Organize;

/// <summary>One file that gets a new name inside the item folder (paths relative to it).</summary>
/// <param name="IsVideo">The library's video file: its record and stored details follow it.</param>
public sealed record FileMove(string From, string To, bool IsVideo);

/// <summary>
/// Moves a movie or series folder (rename, other root folder, other drive) and renames files in it, then updates the
/// library: the folder path, the file records and the stored file details (<see cref="MediaFileDetailsRepository.Move"/>).
/// When a step fails, the steps already done are reversed, so the item is either completely moved or not at all.
/// </summary>
public sealed class ItemRelocator(
    MediaManagementSettings settings,
    MovieRepository movies,
    SeriesRepository series,
    EpisodeFileRepository episodeFiles,
    MediaFileDetailsRepository details,
    FolderMover mover,
    ImportFileSystem files,
    ILogger<ItemRelocator> logger)
{
    /// <param name="fromFolder">The item folder now.</param>
    /// <param name="toFolder">The item folder afterwards (may equal <paramref name="fromFolder"/>).</param>
    /// <param name="moves">Files renamed after the folder moved, relative to the folder.</param>
    /// <param name="rootFolderId">The new root folder, when the item moves to another one.</param>
    /// <param name="progress">How far a copy to another drive is (0..1).</param>
    public void Relocate(
        MediaType mediaType,
        int id,
        string fromFolder,
        string toFolder,
        IReadOnlyList<FileMove> moves,
        int? rootFolderId = null,
        Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(moves);
        var options = settings.Current;
        var movie = mediaType == MediaType.Movie ? movies.Find(id) ?? throw new LibraryException("Movie not found.", LibraryError.NotFound) : null;
        var show = mediaType == MediaType.Series ? series.Find(id) ?? throw new LibraryException("Series not found.", LibraryError.NotFound) : null;
        if (movie is null && show is null)
        {
            throw new LibraryException("Only movies and series can be moved.", LibraryError.Invalid);
        }

        var done = new List<(string From, string To, bool Folder)>();
        try
        {
            if (!string.Equals(fromFolder, toFolder, StringComparison.Ordinal) && mover.MoveFolder(fromFolder, toFolder, options, progress))
            {
                done.Add((fromFolder, toFolder, true));
            }

            foreach (var move in moves)
            {
                var source = Path.Combine(toFolder, move.From);
                var target = Path.Combine(toFolder, move.To);
                mover.MoveFile(source, target, options);
                done.Add((source, target, false));
            }
        }
        catch
        {
            Rollback(done, options);
            throw;
        }

        var videos = moves.Where(m => m.IsVideo).GroupBy(m => Normalize(m.From)).ToDictionary(g => g.Key, g => g.First().To, StringComparer.Ordinal);
        if (movie is not null)
        {
            if (movie.File is { } file)
            {
                movie.File = Moved(file, fromFolder, toFolder, videos);
            }

            movie.FolderPath = toFolder;
            movies.Update(movie);
            if (rootFolderId is { } root)
            {
                movies.SetRootFolder(movie.Id, root);
            }
        }
        else if (show is not null)
        {
            foreach (var group in series.Episodes(show.Id).Where(e => e.File is not null).GroupBy(e => e.File!.RelativePath, StringComparer.Ordinal))
            {
                var file = group.First().File!;
                var moved = Moved(file, fromFolder, toFolder, videos);
                if (!string.Equals(moved.RelativePath, file.RelativePath, StringComparison.Ordinal))
                {
                    episodeFiles.SetFile([.. group.Select(e => e.Id)], moved);
                }
            }

            show.FolderPath = toFolder;
            series.Update(show);
            if (rootFolderId is { } root)
            {
                series.SetRootFolder(show.Id, root);
            }
        }

        if (moves.Count > 0 && options.DeleteEmptyFolders && Directory.Exists(toFolder))
        {
            files.DeleteEmptyFolders(toFolder, includeSelf: false);
        }
    }

    /// <summary>The file record after the move; its stored details (quality, release group) move along.</summary>
    private MediaFile Moved(MediaFile file, string fromFolder, string toFolder, Dictionary<string, string> videos)
    {
        var relative = videos.GetValueOrDefault(Normalize(file.RelativePath)) ?? file.RelativePath;
        var before = Path.Combine(fromFolder, file.RelativePath);
        var after = Path.Combine(toFolder, relative);
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            details.Move(before, after);
        }

        var info = new FileInfo(after);
        return info.Exists ? new MediaFile(relative, info.Length, info.LastWriteTimeUtc) : file with { RelativePath = relative };
    }

    private void Rollback(List<(string From, string To, bool Folder)> done, MediaManagementValues options)
    {
        for (var i = done.Count - 1; i >= 0; i--)
        {
            var (from, to, folder) = done[i];
            try
            {
                if (folder)
                {
                    mover.MoveFolder(to, from, options);
                }
                else
                {
                    mover.MoveFile(to, from, options);
                }
            }
            catch (Exception ex) when (ex is LibraryException or IOException or UnauthorizedAccessException)
            {
                logger.LogError("Could not undo the move of {From} to {To}: {Message}", from, to, ex.Message);
            }
        }
    }

    private static string Normalize(string relative) => relative.Replace('\\', '/');
}
