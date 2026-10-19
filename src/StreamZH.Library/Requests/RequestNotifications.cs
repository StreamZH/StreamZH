// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Events;
using StreamZH.Core.Media;
using StreamZH.Core.Requests;
using StreamZH.Data;
using StreamZH.Library.Notifications;

namespace StreamZH.Library.Requests;

/// <summary>
/// Sends new, approved, declined and available requests to the Connect services whose request triggers are on (all off
/// by default). "Now available" only when Settings → Requests "Notify when available" is on.
/// </summary>
public sealed class RequestNotifier(
    IEventBus events,
    ConnectDispatcher dispatcher,
    RequestRepository requests,
    UserRepository users,
    RequestSettings settings,
    MovieRepository movies,
    SeriesRepository series,
    ILogger<RequestNotifier> logger) : IHostedService
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = events.Subscribe<RequestActivity>((activity, _) =>
        {
            try
            {
                if (Build(activity) is { } connectEvent)
                {
                    return dispatcher.Queue(() => connectEvent);
                }
            }
            catch (Exception ex)
            {
                // A notification must never break the request itself.
                logger.LogWarning(ex, "Could not prepare the notification of request {Id}", activity.RequestId);
            }

            return ValueTask.CompletedTask;
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>The Connect event of an activity; null when the request is gone or the message is switched off.</summary>
    public ConnectEvent? Build(RequestActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (activity.Kind == RequestActivityKind.Available && !settings.Values.NotifyOnAvailable)
        {
            return null;
        }

        if (requests.Find(activity.RequestId) is not { } request)
        {
            return null;
        }

        var requestedBy = request.RequestedBy is { } userId
            ? users.FindAccount(userId)?.Name
            : request.DiscordName is { } discord ? $"{discord} (Discord)" : null;
        return new ConnectEvent
        {
            Type = activity.Kind switch
            {
                RequestActivityKind.Created => ConnectEventType.RequestCreated,
                RequestActivityKind.Approved => ConnectEventType.RequestApproved,
                RequestActivityKind.Declined => ConnectEventType.RequestDeclined,
                _ => ConnectEventType.RequestAvailable,
            },
            MediaType = request.MediaType,
            Media = Media(request),
            Request = new ConnectRequest(
                request.Id,
                JsonNamingPolicy.CamelCase.ConvertName(request.Status.ToString()),
                requestedBy,
                request.Seasons,
                request.Note,
                request.Reason,
                activity.Kind == RequestActivityKind.Created && request.Status != RequestStatus.Pending,
                JsonNamingPolicy.CamelCase.ConvertName(request.Source.ToString())),
        };
    }

    private ConnectMedia Media(MediaRequest request)
    {
        if (request.ItemId is { } id)
        {
            if (request.MediaType == MediaType.Movie && movies.Find(id) is { } movie)
            {
                return new ConnectMedia(movie.Id, movie.Title, movie.Year, movie.TmdbId, movie.ImdbId, null, movie.Overview, movie.Genres, movie.PosterPath, movie.BackdropPath, movie.FolderPath);
            }

            if (request.MediaType == MediaType.Series && series.Find(id) is { } show)
            {
                return new ConnectMedia(show.Id, show.Title, show.Year, show.TmdbId, show.ImdbId, show.TvdbId, show.Overview, show.Genres, show.PosterPath, show.BackdropPath, show.FolderPath);
            }
        }

        return new ConnectMedia(null, request.Title, request.Year ?? 0, request.TmdbId, null, null, null, [], request.PosterPath, null, null);
    }
}
