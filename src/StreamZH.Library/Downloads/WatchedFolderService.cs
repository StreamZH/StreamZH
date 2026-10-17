// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Downloads;
using StreamZH.Library.Search;

namespace StreamZH.Library.Downloads;

/// <summary>
/// SABnzbd's "Watched folder": NZB files dropped into the folder are added to the built-in queue and removed from the
/// folder. Files in a subfolder named after a download category get that category ("movies/x.nzb"); files at the top
/// get "other"; other subfolders are left alone. A file is only taken once it has not changed for a moment (still being
/// copied otherwise). Files that are not valid NZBs are renamed to "*.nzb.failed" so they are not tried again.
/// Polls (every few seconds, see the settings) instead of a file system watcher, which misses events on network shares.
/// </summary>
public sealed class WatchedFolderService(
    DownloadSettings settings,
    DownloadCategoryService categories,
    DownloadEngine engine,
    DownloadEngineOptions options,
    ILogger<WatchedFolderService> logger) : BackgroundService
{
    public const string FailedSuffix = ".failed";

    /// <summary>A file must be unchanged for this long before it is picked up.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    private readonly HashSet<string> _taken = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Scanning the watched folder failed");
            }

            try
            {
                await Task.Delay(settings.WatchInterval, options.Time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Adds every settled NZB in the watched folder. Returns the number of downloads added.</summary>
    public async Task<int> ScanAsync(CancellationToken cancellationToken = default)
    {
        if (settings.WatchFolder is not { } folder || !Directory.Exists(folder))
        {
            return 0;
        }

        var added = 0;
        var now = options.Time.GetUtcNow().UtcDateTime;
        foreach (var (file, category) in Candidates(folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_taken.Contains(file))
            {
                continue;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (!info.Exists || now - info.LastWriteTimeUtc < SettleTime)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            byte[] content;
            try
            {
                content = await File.ReadAllBytesAsync(file, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug("Watched folder: {File} cannot be read yet ({Message})", file, ex.Message);
                continue;
            }

            var (title, password) = NzbPassword.Split(Path.GetFileNameWithoutExtension(file));
            try
            {
                var item = await engine.AddAsync(
                    new DownloadRequest(content, title, DownloadCategoryService.KindOf(category.Name), MediaLink.None, Password: password, CategoryName: category.Name),
                    cancellationToken);
                added++;
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Not removable (read-only share): remember it, so it is not added again while StreamZH runs.
                    _taken.Add(file);
                    logger.LogWarning("Watched folder: {File} was added but could not be removed: {Message}", Path.GetFileName(file), ex.Message);
                }

                logger.LogInformation("Watched folder: added {File} as download {Id} ({Category})", Path.GetFileName(file), item.Id, category.Name);
            }
            catch (LibraryException ex)
            {
                logger.LogWarning("Watched folder: {File} was not added: {Message}", Path.GetFileName(file), ex.Message);
                TryRename(file, file + FailedSuffix);
            }
        }

        return added;
    }

    /// <summary>NZB files at the top (category "other") and in subfolders named after a category.</summary>
    private IEnumerable<(string File, DownloadCategoryDefinition Category)> Candidates(string folder)
    {
        var result = new List<(string, DownloadCategoryDefinition)>();
        var other = categories.Resolve(DownloadCategory.Other, null);
        try
        {
            result.AddRange(NzbFiles(folder).Select(f => (f, other)));
            foreach (var sub in Directory.EnumerateDirectories(folder))
            {
                if (categories.Find(Path.GetFileName(sub)) is { } category)
                {
                    result.AddRange(NzbFiles(sub).Select(f => (f, category)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Watched folder {Folder} cannot be read: {Message}", folder, ex.Message);
        }

        return result;
    }

    private static IEnumerable<string> NzbFiles(string folder) =>
        Directory.EnumerateFiles(folder).Where(f => f.EndsWith(".nzb", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal);

    private void TryRename(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Watched folder: could not rename {File}: {Message}", from, ex.Message);
        }
    }
}
