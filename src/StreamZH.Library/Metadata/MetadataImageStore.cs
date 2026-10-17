// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Library.Metadata;

/// <summary>
/// TMDB artwork on disk: copies for the media folders (poster.jpg, fanart.jpg …) and the local image cache in
/// &lt;data&gt;/MediaCover/&lt;size&gt;/&lt;file&gt; that the API serves when the cache is on.
/// </summary>
public sealed class MetadataImageStore(ITmdbImageClient images, MetadataConfig settings)
{
    /// <summary>Size of the artwork written next to the media (what Radarr and Sonarr use).</summary>
    public const string MediaFolderSize = "original";

    // Shared by all instances (the store is scoped): one download per file at a time.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    /// <summary>The file in the cache for one image; it may not exist yet.</summary>
    public string CachePath(string size, string path) => Path.Combine(settings.ImageCacheDirectory, size, path.TrimStart('/'));

    /// <summary>Writes the image to <paramref name="targetFile"/> unless it exists. False when there was nothing to do.</summary>
    /// <exception cref="TmdbException">The image could not be downloaded.</exception>
    public async Task<bool> SaveAsync(string? tmdbPath, string targetFile, CancellationToken cancellationToken)
    {
        if (tmdbPath is null || !TmdbImages.IsValidPath(tmdbPath) || File.Exists(targetFile))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        if (settings.ImageCache)
        {
            var cached = await GetCachedAsync(MediaFolderSize, tmdbPath, cancellationToken);
            await WriteAtomicAsync(targetFile, async stream =>
            {
                await using var source = File.OpenRead(cached);
                await source.CopyToAsync(stream, cancellationToken);
            });
            return true;
        }

        await WriteAtomicAsync(targetFile, stream => images.DownloadAsync(tmdbPath, MediaFolderSize, stream, cancellationToken));
        return true;
    }

    /// <summary>Path of the cached image, downloaded first when it is not in the cache yet.</summary>
    /// <exception cref="TmdbException">Invalid size or path, or the download failed.</exception>
    public async Task<string> GetCachedAsync(string size, string path, CancellationToken cancellationToken)
    {
        if (!TmdbImages.Sizes.Contains(size) || !TmdbImages.IsValidPath(path))
        {
            throw new TmdbException($"Not a TMDB image: {size}{path}", 400);
        }

        var file = CachePath(size, path);
        if (File.Exists(file))
        {
            return file;
        }

        var gate = Locks.GetOrAdd(file, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await WriteAtomicAsync(file, stream => images.DownloadAsync(path, size, stream, cancellationToken));
            }

            return file;
        }
        finally
        {
            gate.Release();
            Locks.TryRemove(file, out _);
        }
    }

    /// <summary>Writes into a temporary file next to the target and renames it, so nobody sees half an image.</summary>
    private static async Task WriteAtomicAsync(string target, Func<Stream, Task> write)
    {
        var temp = $"{target}.{Guid.NewGuid():N}.partial";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await write(stream);
            }

            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
