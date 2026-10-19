// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Events;
using StreamZH.Core.Jobs;
using StreamZH.Core.Media;
using StreamZH.Core.Requests;
using StreamZH.Data;

namespace StreamZH.Library.Requests;

/// <summary>How much of a movie or of some seasons of a series is in the library with files.</summary>
public enum LibraryAvailability
{
    /// <summary>Nothing (or nothing aired yet).</summary>
    None,

    /// <summary>Some aired episodes of the seasons have files.</summary>
    Partial,

    /// <summary>The movie has a file; every aired episode of the seasons has a file.</summary>
    Full,
}

/// <summary>
/// Keeps approved requests in step with the library (0.10): a movie with a file, or a series whose requested seasons
/// have a file for every aired episode, makes the request available; some files make it partially available; an item
/// removed from the library makes it fail. Runs after imports and library changes and every 15 minutes.
/// </summary>
public sealed class RequestAvailability(
    RequestRepository requests,
    MovieRepository movies,
    SeriesRepository series,
    IEventBus events,
    TimeProvider time,
    ILogger<RequestAvailability> logger)
{
    public const string RemovedReason = "Removed from the library.";

    /// <summary>States in which a request waits for files.</summary>
    public static readonly RequestStatus[] Watched = [RequestStatus.Approved, RequestStatus.Processing, RequestStatus.PartiallyAvailable];

    /// <summary>Movie: has a file.</summary>
    public static LibraryAvailability Of(Movie movie)
    {
        ArgumentNullException.ThrowIfNull(movie);
        return movie.File is null ? LibraryAvailability.None : LibraryAvailability.Full;
    }

    /// <summary>
    /// Series: aired episodes (air date up to <paramref name="today"/>) of the seasons (null = every season) and how many
    /// of them have files. Specials (season 0) never count; seasons without aired episodes count as nothing.
    /// </summary>
    public static LibraryAvailability Of(IEnumerable<Episode> episodes, IReadOnlyCollection<int>? seasons, string today)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        var aired = episodes
            .Where(e => e.SeasonNumber > 0 && (seasons is null || seasons.Contains(e.SeasonNumber)))
            .Where(e => e.AirDate is { } date && string.CompareOrdinal(date, today) <= 0)
            .ToList();
        if (aired.Count == 0)
        {
            return LibraryAvailability.None;
        }

        var withFiles = aired.Count(e => e.File is not null);
        return withFiles == aired.Count ? LibraryAvailability.Full : withFiles > 0 ? LibraryAvailability.Partial : LibraryAvailability.None;
    }

    /// <summary>Seasons (above 0) of a library series whose aired episodes all have files.</summary>
    public static IReadOnlySet<int> AvailableSeasons(IEnumerable<Episode> episodes, string today)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        return episodes.Where(e => e.SeasonNumber > 0)
            .GroupBy(e => e.SeasonNumber)
            .Where(g => Of(g, null, today) == LibraryAvailability.Full)
            .Select(g => g.Key)
            .ToHashSet();
    }

    public string Today => time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Checks every request that waits for files; returns how many changed.</summary>
    public async Task<int> CheckAllAsync(CancellationToken cancellationToken = default)
    {
        var changed = 0;
        foreach (var request in requests.InStates(Watched))
        {
            cancellationToken.ThrowIfCancellationRequested();
            changed += await CheckAsync(request, cancellationToken) is { } after && after.Status != request.Status ? 1 : 0;
        }

        return changed;
    }

    /// <summary>Checks the requests of one library item (all items when <paramref name="itemId"/> is null).</summary>
    public async Task CheckItemAsync(MediaType mediaType, int? itemId, CancellationToken cancellationToken = default)
    {
        foreach (var request in requests.InStates(Watched).Where(r => r.MediaType == mediaType && r.ItemId is not null && (itemId is null || r.ItemId == itemId)))
        {
            await CheckAsync(request, cancellationToken);
        }
    }

    /// <summary>Re-evaluates one request (only in <see cref="Watched"/> states with a library item) and stores a change.</summary>
    public async Task<MediaRequest> CheckAsync(MediaRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Watched.Contains(request.Status) || request.ItemId is not { } itemId)
        {
            return request;
        }

        var now = time.GetUtcNow().UtcDateTime;
        MediaRequest updated;
        var availability = Evaluate(request.MediaType, itemId, request.Seasons);
        if (availability is null)
        {
            updated = request with { Status = RequestStatus.Failed, Reason = RemovedReason, Updated = now };
        }
        else
        {
            var status = availability switch
            {
                LibraryAvailability.Full => RequestStatus.Available,
                LibraryAvailability.Partial => RequestStatus.PartiallyAvailable,
                _ => RequestStatus.Processing,
            };

            // Approved stays approved until the item is in the library and processing started; a request never goes back
            // from partially available to processing just because a file was replaced in between.
            if (status == request.Status || (status == RequestStatus.Processing && request.Status != RequestStatus.Approved))
            {
                return request;
            }

            updated = request with { Status = status, Updated = now, Available = status == RequestStatus.Available ? now : request.Available };
        }

        if (updated.Status == request.Status)
        {
            return request;
        }

        requests.Update(updated);
        logger.LogInformation("Request {Id} '{Title}' is now {Status}", request.Id, request.Title, updated.Status);
        await events.PublishAsync(new RequestChanged(updated.Id, updated.Status), cancellationToken);
        if (updated.Status == RequestStatus.Available)
        {
            await events.PublishAsync(new RequestActivity(RequestActivityKind.Available, updated.Id), cancellationToken);
        }

        return updated;
    }

    /// <summary>Availability of a library item; null when it is not in the library (any more).</summary>
    public LibraryAvailability? Evaluate(MediaType mediaType, int itemId, IReadOnlyCollection<int>? seasons)
    {
        if (mediaType == MediaType.Movie)
        {
            return movies.Find(itemId) is { } movie ? Of(movie) : null;
        }

        return series.Find(itemId) is null ? null : Of(series.Episodes(itemId), seasons, Today);
    }
}

/// <summary>Checks requests after imports and library changes (the event handlers run inline, so this stays cheap).</summary>
public sealed class RequestAvailabilityWatcher(IEventBus events, RequestAvailability availability, ILogger<RequestAvailabilityWatcher> logger) : IHostedService
{
    private readonly List<IDisposable> _subscriptions = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_subscriptions)
        {
            _subscriptions.Add(events.Subscribe<LibraryChanged>((e, ct) => CheckAsync(e.MediaType, e.Id, ct)));
            _subscriptions.Add(events.Subscribe<MediaImported>((e, ct) =>
                CheckAsync(e.MediaType, e.MediaType == MediaType.Movie ? e.MovieId : e.SeriesId, ct)));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_subscriptions)
        {
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
        }

        return Task.CompletedTask;
    }

    private async ValueTask CheckAsync(MediaType mediaType, int? itemId, CancellationToken cancellationToken)
    {
        try
        {
            await availability.CheckItemAsync(mediaType, itemId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never break an import or a library change because of a request.
            logger.LogWarning(ex, "Could not check the requests of {Type} {Id}", mediaType, itemId);
        }
    }
}

/// <summary>System → Tasks: "Request availability", every 15 minutes (episodes air, files appear by other ways).</summary>
public sealed class RequestAvailabilityTask : IScheduledTask
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(15);

    public string Key => "request-availability";

    public string Name => "Request availability";

    public TimeSpan? Interval => DefaultInterval;

    public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<RequestAvailability>().CheckAllAsync(cancellationToken);
}
