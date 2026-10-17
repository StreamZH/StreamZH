// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Events;
using StreamZH.Core.Indexers;
using StreamZH.Core.Quality;
using StreamZH.Data;

namespace StreamZH.Library.Search;

/// <summary>
/// Grabs a release: fetches the NZB from the indexer, writes it to the NZB folder and records it in the history.
/// Until the download engine arrives (0.5) the NZB folder is the hand-off point (e.g. a SABnzbd watch folder).
/// </summary>
public sealed class GrabService(
    IIndexerProvider indexers,
    IIndexerClient client,
    ReleaseCache cache,
    DownloadSettings settings,
    HistoryRepository history,
    IEventBus events,
    ILogger<GrabService> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Grabs a release from the latest search results. Rejected releases may be grabbed too (the user decides).</summary>
    /// <exception cref="LibraryException">The release is not in the cache (any more) or its indexer is gone.</exception>
    /// <exception cref="IndexerException">The indexer did not deliver an NZB; the message is meant for the user.</exception>
    public Task<GrabResult> GrabAsync(int indexerId, string guid, CancellationToken cancellationToken = default)
    {
        var decision = cache.Find(indexerId, guid)
            ?? throw new LibraryException("This release is no longer in the search results. Search again.", LibraryError.NotFound);
        return GrabAsync(decision, cancellationToken);
    }

    public async Task<GrabResult> GrabAsync(ReleaseDecision decision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var release = decision.Release;
        var indexer = indexers.Find(release.IndexerId)
            ?? throw new LibraryException("The indexer of this release no longer exists.", LibraryError.NotFound);

        byte[] nzb;
        try
        {
            nzb = await client.DownloadNzbAsync(indexer, release, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            indexers.RecordFailure(indexer.Id, ex);
            logger.LogWarning("Could not download the NZB of '{Title}' from {Indexer}: {Message}", release.Title, indexer.Name, ex.Message);
            throw new IndexerException(
                $"{indexer.Name} did not deliver the NZB: {ex.Message}",
                (ex as IndexerException)?.Code,
                (ex as IndexerException)?.RetryAfter,
                ex);
        }

        if (!LooksLikeNzb(nzb))
        {
            var error = new IndexerException($"{indexer.Name} did not return an NZB file (maybe an error page or a request limit).");
            indexers.RecordFailure(indexer.Id, error);
            logger.LogWarning("'{Title}' from {Indexer} is not an NZB file ({Bytes} bytes)", release.Title, indexer.Name, nzb.Length);
            throw error;
        }

        indexers.RecordSuccess(indexer.Id);

        var folder = settings.NzbFolder;
        string path;
        try
        {
            path = NzbFileNames.WriteNew(folder, release.Title, nzb);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LibraryException($"Could not write the NZB file to {folder}: {ex.Message}", LibraryError.Invalid);
        }

        var parsed = decision.Parsed;
        var subject = decision.Subject;
        history.Add(new HistoryItem
        {
            Date = _time.GetUtcNow().UtcDateTime,
            EventType = HistoryEventType.Grabbed,
            MediaType = subject.MediaType,
            MovieId = subject.MovieId,
            SeriesId = subject.SeriesId,
            EpisodeIds = decision.EpisodeIds,
            SourceTitle = release.Title,
            QualityId = parsed?.Quality.Quality.Id ?? Quality.Unknown.Id,
            LanguageIds = [.. (parsed?.Languages ?? []).Select(l => l.Id)],
            ReleaseGroup = parsed?.ReleaseGroup,
            Indexer = indexer.Name,
            IndexerId = indexer.Id,
            Size = release.Size,
            Data = new Dictionary<string, string?>
            {
                ["guid"] = release.Guid,
                ["downloadUrl"] = release.DownloadUrl,
                ["infoUrl"] = release.InfoUrl,
                ["publishDate"] = release.PublishDate.ToString("O", CultureInfo.InvariantCulture),
                ["nzbPath"] = path,
                ["accepted"] = decision.Accepted ? "true" : "false",
                ["rejections"] = decision.Accepted ? null : string.Join("; ", decision.Rejections.Select(r => r.Message)),
            },
        });

        logger.LogInformation("Grabbed '{Title}' from {Indexer} for {Subject}, NZB saved to {Path}", release.Title, indexer.Name, subject.Title, path);
        await events.PublishAsync(new ReleaseGrabbed(subject.MediaType, subject.MovieId, subject.SeriesId, release.Title, indexer.Name), cancellationToken);
        return new GrabResult(release.Title, indexer.Name, path);
    }

    /// <summary>An NZB is XML with an &lt;nzb&gt; root; indexers sometimes answer with HTML or JSON errors and status 200.</summary>
    internal static bool LooksLikeNzb(byte[]? content)
    {
        if (content is null || content.Length == 0)
        {
            return false;
        }

        var head = Encoding.UTF8.GetString(content, 0, Math.Min(content.Length, 4096));
        return head.Contains("<nzb", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Safe, unique NZB file names from release titles.</summary>
public static class NzbFileNames
{
    private const int MaxNameLength = 180;

    // Invalid on Windows, macOS or Linux; the NZB folder may be a share used by another OS.
    private static readonly char[] Invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public static string SafeName(string title)
    {
        var builder = new StringBuilder(title.Length);
        foreach (var c in title.Trim())
        {
            builder.Append(char.IsControl(c) || Invalid.Contains(c) ? '_' : c);
        }

        var name = builder.ToString().Trim().TrimEnd('.', ' ');
        if (name.Length > MaxNameLength)
        {
            name = name[..MaxNameLength].TrimEnd('.', ' ');
        }

        return name.Length == 0 || name.All(c => c == '.' || c == '_') ? "release" : name;
    }

    /// <summary>Writes the content to "&lt;title&gt;.nzb", or "&lt;title&gt; (2).nzb" etc. when that exists. Never overwrites.</summary>
    public static string WriteNew(string folder, string title, byte[] content)
    {
        Directory.CreateDirectory(folder);
        var name = SafeName(title);

        for (var attempt = 1; ; attempt++)
        {
            var path = Path.Combine(folder, attempt == 1 ? $"{name}.nzb" : $"{name} ({attempt}).nzb");
            try
            {
                // CreateNew fails if the file exists, so two grabs at the same time cannot overwrite each other.
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(content);
                return path;
            }
            catch (IOException) when (File.Exists(path) && attempt < 1000)
            {
            }
        }
    }
}
