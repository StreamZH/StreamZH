// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Nodes;
using StreamZH.Core;

namespace StreamZH.Library.Notifications;

/// <summary>
/// Sends every event as JSON in the layout of Radarr's and Sonarr's webhooks (eventType, movie or series, episodes,
/// release, movieFile/episodeFiles, isUpgrade …), so existing scripts keep working. The schema is in docs/WEBHOOK.md.
/// </summary>
public sealed class WebhookSender(IHttpClientFactory httpFactory) : IConnectionSender
{
    public string Implementation => ConnectCatalog.Webhook;

    public Task SendAsync(ConnectionContext context, ConnectEvent connectEvent, CancellationToken cancellationToken) =>
        PostAsync(context, connectEvent, cancellationToken);

    public async Task<string> TestAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
        await PostAsync(context, ConnectEvent.Test(), cancellationToken);
        return "Test event sent to the webhook.";
    }

    /// <summary>
    /// "Grab", "Download" (import and upgrade), "Rename", "DownloadFailure", "Health", "HealthRestored", "Test"; requests
    /// (0.10): "RequestCreated", "RequestApproved", "RequestDeclined", "RequestAvailable".
    /// </summary>
    public static string EventType(ConnectEventType type) => type switch
    {
        ConnectEventType.Grab => "Grab",
        ConnectEventType.Import or ConnectEventType.Upgrade => "Download",
        ConnectEventType.Rename => "Rename",
        ConnectEventType.DownloadFailure => "DownloadFailure",
        ConnectEventType.HealthIssue => "Health",
        ConnectEventType.HealthRestored => "HealthRestored",
        ConnectEventType.RequestCreated => "RequestCreated",
        ConnectEventType.RequestApproved => "RequestApproved",
        ConnectEventType.RequestDeclined => "RequestDeclined",
        ConnectEventType.RequestAvailable => "RequestAvailable",
        _ => "Test",
    };

    public static JsonObject Payload(ConnectEvent e, string instanceName)
    {
        ArgumentNullException.ThrowIfNull(e);
        var payload = new JsonObject
        {
            ["eventType"] = EventType(e.Type),
            ["instanceName"] = instanceName,
            ["applicationName"] = AppInfo.Name,
            ["applicationVersion"] = AppInfo.Version,
        };

        if (e.Media is { } media)
        {
            var images = new JsonArray();
            if (media.PosterUrl is { } poster)
            {
                images.Add(new JsonObject { ["coverType"] = "poster", ["remoteUrl"] = poster });
            }

            if (media.FanartUrl is { } fanart)
            {
                images.Add(new JsonObject { ["coverType"] = "fanart", ["remoteUrl"] = fanart });
            }

            var item = new JsonObject
            {
                ["id"] = media.Id,
                ["title"] = media.Title,
                ["year"] = media.Year,
                [e.IsSeries ? "path" : "folderPath"] = media.Folder,
                ["tmdbId"] = media.TmdbId,
                ["imdbId"] = media.ImdbId,
                ["overview"] = media.Overview,
                ["genres"] = new JsonArray([.. media.Genres.Select(g => (JsonNode?)JsonValue.Create(g))]),
                ["images"] = images,
            };
            if (e.IsSeries)
            {
                item["tvdbId"] = media.TvdbId;
            }

            payload[e.IsSeries ? "series" : "movie"] = item;
        }

        if (e.Episodes.Count > 0)
        {
            payload["episodes"] = new JsonArray([.. e.Episodes.Select(ep => (JsonNode?)new JsonObject
            {
                ["id"] = ep.Id,
                ["seasonNumber"] = ep.SeasonNumber,
                ["episodeNumber"] = ep.EpisodeNumber,
                ["title"] = ep.Title,
                ["airDate"] = ep.AirDate,
                ["overview"] = ep.Overview,
            })]);
        }

        if (e.Release is { } release && e.Type != ConnectEventType.Rename)
        {
            payload["release"] = new JsonObject
            {
                ["releaseTitle"] = release.Title,
                ["quality"] = release.Quality,
                ["releaseGroup"] = release.ReleaseGroup,
                ["indexer"] = release.Indexer,
                ["size"] = release.Size,
            };
        }

        if (e.Files.Count > 0)
        {
            JsonObject File(ConnectFile f) => new()
            {
                ["path"] = f.Path,
                ["relativePath"] = f.RelativePath,
                ["size"] = f.Size,
                ["quality"] = e.Release?.Quality,
                ["releaseGroup"] = e.Release?.ReleaseGroup,
                ["sceneName"] = e.Release?.Title,
            };

            if (e.IsSeries)
            {
                payload["episodeFiles"] = new JsonArray([.. e.Files.Select(f => (JsonNode?)File(f))]);
            }
            else
            {
                payload["movieFile"] = File(e.Files[0]);
            }
        }

        if (e.Type is ConnectEventType.Import or ConnectEventType.Upgrade)
        {
            payload["isUpgrade"] = e.IsUpgrade;
        }

        if (e.DownloadId is { } downloadId)
        {
            payload["downloadId"] = downloadId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (e.Type == ConnectEventType.DownloadFailure)
        {
            payload["message"] = e.Message;
        }

        if (e.Request is { } request)
        {
            payload["request"] = new JsonObject
            {
                ["id"] = request.Id,
                ["status"] = request.Status,
                ["requestedBy"] = request.RequestedBy,
                ["source"] = request.Source,
                ["seasons"] = request.Seasons is null ? null : new JsonArray([.. request.Seasons.Select(n => (JsonNode?)JsonValue.Create(n))]),
                ["note"] = request.Note,
                ["reason"] = request.Reason,
                ["automatic"] = request.Automatic,
            };
        }

        if (e.Health is { } health)
        {
            payload["level"] = health.Level == "error" ? "error" : "warning";
            payload["message"] = string.IsNullOrWhiteSpace(health.Message) ? health.Title : $"{health.Title}: {health.Message}";
            payload["type"] = health.Key;
            payload["wikiUrl"] = null;
        }

        return payload;
    }

    private async Task PostAsync(ConnectionContext context, ConnectEvent e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.Settings;
        var url = settings.Text("url") ?? throw new ConnectException("URL is missing");
        var method = settings.Text("method") == "PUT" ? HttpMethod.Put : HttpMethod.Post;
        var http = httpFactory.CreateClient(ConnectHttp.ClientName);
        using var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(Payload(e, context.InstanceName).ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        if (settings.Text("username") is { } user)
        {
            request.Headers.Authorization = ConnectHttp.Basic(user, settings.Text("password"));
        }

        foreach (var line in (settings.Text("headers") ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                request.Headers.TryAddWithoutValidation(line[..colon].Trim(), line[(colon + 1)..].Trim());
            }
        }

        await ConnectHttp.SendAsync(http, request, cancellationToken, ConnectHttp.Short);
    }
}
