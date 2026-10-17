// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using StreamZH.Core;

namespace StreamZH.Library.Notifications;

/// <summary>
/// Plex Media Server: after an import or rename the folder is scanned in the Plex libraries whose locations contain it
/// (partial scan by path, like Radarr); when no library location matches, all libraries of that type are scanned.
/// </summary>
public sealed class PlexSender(IHttpClientFactory httpFactory, ILogger<PlexSender> logger) : IConnectionSender
{
    public string Implementation => ConnectCatalog.Plex;

    public async Task SendAsync(ConnectionContext context, ConnectEvent connectEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(connectEvent);
        if (!context.Settings.Bool("updateLibrary") || (connectEvent.Folder ?? connectEvent.Media?.Folder) is not { } folder)
        {
            return;
        }

        var path = ConnectPaths.Map(folder, context.Settings.Text("mapFrom"), context.Settings.Text("mapTo"));
        var type = connectEvent.IsSeries ? "show" : "movie";
        var sections = (await SectionsAsync(context, cancellationToken)).Where(s => s.Type == type).ToList();
        var matching = sections.Where(s => s.Locations.Any(l => ConnectPaths.IsInside(path, l))).ToList();
        if (matching.Count == 0)
        {
            logger.LogInformation("{Connection}: no Plex library contains {Path}, scanning all {Type} libraries", context.Definition.Name, path, type);
            foreach (var section in sections)
            {
                await GetAsync(context, $"library/sections/{Uri.EscapeDataString(section.Key)}/refresh", cancellationToken);
            }

            return;
        }

        foreach (var section in matching)
        {
            await GetAsync(context, $"library/sections/{Uri.EscapeDataString(section.Key)}/refresh?path={Uri.EscapeDataString(path)}", cancellationToken);
        }
    }

    public async Task<string> TestAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sections = await SectionsAsync(context, cancellationToken);
        var movies = sections.Count(s => s.Type == "movie");
        var shows = sections.Count(s => s.Type == "show");
        return $"Connected to Plex: {movies} movie and {shows} TV {(shows == 1 ? "library" : "libraries")}.";
    }

    /// <summary>One Plex library ("section") with its folders as Plex sees them.</summary>
    public sealed record PlexSection(string Key, string Type, string Title, IReadOnlyList<string> Locations);

    public async Task<IReadOnlyList<PlexSection>> SectionsAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var body = await GetAsync(context, "library/sections", cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("MediaContainer", out var container)
                || !container.TryGetProperty("Directory", out var directories)
                || directories.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return [.. directories.EnumerateArray().Select(d => new PlexSection(
                d.TryGetProperty("key", out var key) ? key.ToString() : string.Empty,
                d.TryGetProperty("type", out var type) ? type.GetString() ?? string.Empty : string.Empty,
                d.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                d.TryGetProperty("Location", out var locations) && locations.ValueKind == JsonValueKind.Array
                    ? [.. locations.EnumerateArray().Select(l => l.TryGetProperty("path", out var p) ? p.GetString() : null).OfType<string>()]
                    : []))];
        }
        catch (JsonException ex)
        {
            throw new ConnectException("unexpected answer, is this a Plex server?", inner: ex);
        }
    }

    private async Task<string> GetAsync(ConnectionContext context, string relative, CancellationToken cancellationToken)
    {
        var http = httpFactory.CreateClient(ConnectHttp.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ConnectHttp.HostUri(context.Settings, 32400), relative));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("X-Plex-Token", context.Settings.Text("authToken") ?? string.Empty);
        request.Headers.Add("X-Plex-Product", AppInfo.Name);
        request.Headers.Add("X-Plex-Version", AppInfo.Version);
        request.Headers.Add("X-Plex-Client-Identifier", $"streamzh-{context.Definition.Id.ToString(CultureInfo.InvariantCulture)}");
        return await ConnectHttp.SendAsync(http, request, cancellationToken);
    }
}

/// <summary>
/// Jellyfin and Emby (same API family): report the changed folder ("Library/Media/Updated") or start a full scan;
/// Emby can also show a notification to its admins.
/// </summary>
public sealed class JellyfinEmbySender(string implementation, IHttpClientFactory httpFactory) : IConnectionSender
{
    public string Implementation => implementation;

    private bool IsEmby => implementation == ConnectCatalog.Emby;

    public async Task SendAsync(ConnectionContext context, ConnectEvent connectEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(connectEvent);
        var settings = context.Settings;
        var library = connectEvent.Type is ConnectEventType.Import or ConnectEventType.Upgrade or ConnectEventType.Rename;

        if (IsEmby && settings.Bool("sendNotifications") && connectEvent.Type != ConnectEventType.Rename)
        {
            await NotifyAsync(context, connectEvent, cancellationToken);
        }

        if (!library || !settings.Bool("updateLibrary"))
        {
            return;
        }

        var folder = connectEvent.Folder ?? connectEvent.Media?.Folder;
        if (settings.Text("updateMode") == "full" || folder is null)
        {
            await PostAsync(context, "Library/Refresh", null, cancellationToken);
            return;
        }

        var path = ConnectPaths.Map(folder, settings.Text("mapFrom"), settings.Text("mapTo"));
        await PostAsync(context, "Library/Media/Updated", new { Updates = new[] { new { Path = path, UpdateType = "Modified" } } }, cancellationToken);
    }

    public async Task<string> TestAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var http = httpFactory.CreateClient(ConnectHttp.ClientName);
        using var request = Request(context, HttpMethod.Get, "System/Info");
        var body = await ConnectHttp.SendAsync(http, request, cancellationToken);
        string? name = null;
        string? version = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            name = document.RootElement.TryGetProperty("ServerName", out var n) ? n.GetString() : null;
            version = document.RootElement.TryGetProperty("Version", out var v) ? v.GetString() : null;
        }
        catch (JsonException ex)
        {
            throw new ConnectException($"unexpected answer, is this a {(IsEmby ? "Emby" : "Jellyfin")} server?", inner: ex);
        }

        if (IsEmby && context.Settings.Bool("sendNotifications"))
        {
            await NotifyAsync(context, ConnectEvent.Test(), cancellationToken);
        }

        return $"Connected to {name ?? (IsEmby ? "Emby" : "Jellyfin")}{(version is null ? string.Empty : " " + version)}.";
    }

    private Task NotifyAsync(ConnectionContext context, ConnectEvent connectEvent, CancellationToken cancellationToken) =>
        PostAsync(
            context,
            "Notifications/Admin",
            new { Name = connectEvent.Subject, Description = connectEvent.Summary, ImageUrl = connectEvent.Media?.PosterUrl },
            cancellationToken);

    private async Task PostAsync(ConnectionContext context, string relative, object? body, CancellationToken cancellationToken)
    {
        var http = httpFactory.CreateClient(ConnectHttp.ClientName);
        using var request = Request(context, HttpMethod.Post, relative);
        if (body is not null)
        {
            request.Content = ConnectHttp.JsonContent(body, new JsonSerializerOptions());
        }

        await ConnectHttp.SendAsync(http, request, cancellationToken);
    }

    private HttpRequestMessage Request(ConnectionContext context, HttpMethod method, string relative)
    {
        var request = new HttpRequestMessage(method, new Uri(ConnectHttp.BaseUri(context.Settings, "url"), relative));
        var key = context.Settings.Text("apiKey") ?? string.Empty;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (IsEmby)
        {
            request.Headers.Add("X-Emby-Token", key);
        }
        else
        {
            // Jellyfin 10.11 turns the old X-Emby-Token header off by default; this form works on all versions.
            request.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Client=\"{AppInfo.Name}\", Version=\"{AppInfo.Version}\", Token=\"{key}\"");
        }

        return request;
    }
}

/// <summary>
/// Kodi over JSON-RPC: GUI notifications, a library scan of the changed folder (skipped while a video plays unless
/// "Always update" is on) and an optional clean.
/// </summary>
public sealed class KodiSender(IHttpClientFactory httpFactory, ILogger<KodiSender> logger) : IConnectionSender
{
    public string Implementation => ConnectCatalog.Kodi;

    public async Task SendAsync(ConnectionContext context, ConnectEvent connectEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(connectEvent);
        var settings = context.Settings;
        if (settings.Bool("notify") && connectEvent.Type != ConnectEventType.Rename)
        {
            await NotifyAsync(context, connectEvent, cancellationToken);
        }

        if (connectEvent.Type is not (ConnectEventType.Import or ConnectEventType.Upgrade or ConnectEventType.Rename) || !settings.Bool("updateLibrary"))
        {
            return;
        }

        if (!settings.Bool("alwaysUpdate"))
        {
            var players = await CallAsync(context, "Player.GetActivePlayers", null, cancellationToken);
            if (players is JsonArray list && list.Any(p => p?["type"]?.GetValue<string>() == "video"))
            {
                logger.LogInformation("{Connection}: a video is playing, Kodi library update skipped", context.Definition.Name);
                return;
            }
        }

        var folder = connectEvent.Folder ?? connectEvent.Media?.Folder;
        if (folder is null)
        {
            await CallAsync(context, "VideoLibrary.Scan", new JsonObject { ["showdialogs"] = false }, cancellationToken);
        }
        else
        {
            var path = ConnectPaths.Map(folder, settings.Text("mapFrom"), settings.Text("mapTo"));
            var separator = path.Contains('\\', StringComparison.Ordinal) && !path.Contains('/', StringComparison.Ordinal) ? "\\" : "/";
            var directory = path.EndsWith(separator, StringComparison.Ordinal) ? path : path + separator;
            await CallAsync(context, "VideoLibrary.Scan", new JsonObject { ["directory"] = directory, ["showdialogs"] = false }, cancellationToken);
        }

        if (settings.Bool("cleanLibrary"))
        {
            await CallAsync(context, "VideoLibrary.Clean", new JsonObject { ["showdialogs"] = false }, cancellationToken);
        }
    }

    public async Task<string> TestAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var version = await CallAsync(context, "JSONRPC.Version", null, cancellationToken);
        if (context.Settings.Bool("notify"))
        {
            await NotifyAsync(context, ConnectEvent.Test(), cancellationToken);
        }

        var v = version?["version"];
        return v is null ? "Connected to Kodi." : $"Connected to Kodi (JSON-RPC {v["major"]}.{v["minor"]}).";
    }

    private Task NotifyAsync(ConnectionContext context, ConnectEvent connectEvent, CancellationToken cancellationToken)
    {
        var parameters = new JsonObject
        {
            ["title"] = $"{AppInfo.Name} - {connectEvent.Subject}",
            ["message"] = connectEvent.Summary,
            ["displaytime"] = (context.Settings.Int("displayTime") ?? 5) * 1000,
        };
        if (connectEvent.Media?.PosterUrl is { } poster)
        {
            parameters["image"] = poster;
        }

        return CallAsync(context, "GUI.ShowNotification", parameters, cancellationToken);
    }

    /// <summary>Calls a JSON-RPC method and returns its "result".</summary>
    private async Task<JsonNode?> CallAsync(ConnectionContext context, string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var http = httpFactory.CreateClient(ConnectHttp.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ConnectHttp.HostUri(context.Settings, 8080), "jsonrpc"));
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["id"] = 1 };
        if (parameters is not null)
        {
            body["params"] = parameters;
        }

        request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        if (context.Settings.Text("username") is { } user)
        {
            request.Headers.Authorization = ConnectHttp.Basic(user, context.Settings.Text("password"));
        }

        var answer = await ConnectHttp.SendAsync(http, request, cancellationToken);
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(answer);
        }
        catch (JsonException ex)
        {
            throw new ConnectException("unexpected answer, is JSON-RPC over HTTP turned on in Kodi?", inner: ex);
        }

        if (node?["error"] is { } error)
        {
            throw new ConnectException($"rejected: {error["message"]?.GetValue<string>() ?? "error"} ({method})");
        }

        return node?["result"];
    }
}
