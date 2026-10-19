// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Core.Indexers;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Library.Downloads;

namespace StreamZH.Library.Search;

/// <summary>
/// Grabs a release: fetches the NZB from the indexer, hands it to the download client chosen in the settings (the
/// built-in engine, or the blackhole folder another downloader watches) and records it in the history.
/// </summary>
public sealed class GrabService(
    IIndexerProvider indexers,
    IIndexerClient client,
    ReleaseCache cache,
    DownloadClientProvider clients,
    DownloadSettings settings,
    HistoryRepository history,
    IEventBus events,
    ILogger<GrabService> logger,
    TimeProvider? timeProvider = null,
    AutomationHold? automationHold = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Grabs a release from the latest search results. Rejected releases may be grabbed too (the user decides).</summary>
    /// <exception cref="LibraryException">The release is not in the cache (any more) or its indexer is gone.</exception>
    /// <exception cref="IndexerException">The indexer did not deliver an NZB; the message is meant for the user.</exception>
    public Task<GrabResult> GrabAsync(int indexerId, string guid, CancellationToken cancellationToken = default) =>
        GrabAsync(indexerId, guid, null, cancellationToken);

    /// <param name="categoryName">Download category for the built-in engine (Search page); null = by the media type.</param>
    public Task<GrabResult> GrabAsync(int indexerId, string guid, string? categoryName, CancellationToken cancellationToken = default)
    {
        var decision = cache.Find(indexerId, guid)
            ?? throw new LibraryException("This release is no longer in the search results. Search again.", LibraryError.NotFound);
        return GrabAsync(decision, categoryName, cancellationToken);
    }

    public Task<GrabResult> GrabAsync(ReleaseDecision decision, CancellationToken cancellationToken = default) => GrabAsync(decision, null, cancellationToken);

    public async Task<GrabResult> GrabAsync(ReleaseDecision decision, string? categoryName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);

        // Side-by-side mode: RSS sync and automatic searches grab nothing; grabs by hand still work.
        if (automationHold?.Blocks == true)
        {
            logger.LogInformation("Not grabbing '{Title}': side-by-side mode is on", decision.Release.Title);
            throw new LibraryException(AutomationHold.Message, LibraryError.Invalid);
        }

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

        var subject = decision.Subject;
        var (title, password) = NzbPassword.Split(release.Title);
        var category = subject.MediaType switch
        {
            MediaType.Movie => DownloadCategory.Movie,
            MediaType.Series => DownloadCategory.Series,
            _ => DownloadCategory.Other,
        };
        var downloadClient = clients.Current;
        var request = new DownloadRequest(
            nzb,
            title,
            category,
            new MediaLink(subject.MediaType, subject.MovieId, subject.SeriesId, decision.EpisodeIds),
            Indexer: indexer.Name,
            Password: password,
            CategoryName: string.IsNullOrWhiteSpace(categoryName) ? null : categoryName.Trim());

        // The engine answers with the download id, the blackhole folder with the written file.
        var reference = await downloadClient.AddAsync(request, cancellationToken);
        var builtIn = downloadClient is BuiltInDownloadClient;
        var path = builtIn ? Path.Combine(settings.NzbQueueFolder, $"{reference}.nzb") : reference;

        var parsed = decision.Parsed;
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
                ["downloadClient"] = downloadClient.Name,
                ["downloadId"] = builtIn ? reference : null,
                ["accepted"] = decision.Accepted ? "true" : "false",
                ["rejections"] = decision.Accepted ? null : string.Join("; ", decision.Rejections.Select(r => r.Message)),
            },
        });

        logger.LogInformation(
            "Grabbed '{Title}' from {Indexer} for {Subject}, handed to {Client} ({Reference})", release.Title, indexer.Name, subject.Title, downloadClient.Name, reference);
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
