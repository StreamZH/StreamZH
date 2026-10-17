// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using StreamZH.Core;

namespace StreamZH.Library.Notifications;

/// <summary>The plain-text body most notification services show under the subject.</summary>
public static class ConnectText
{
    /// <summary>
    /// "Dune (2021) · WEBDL-1080p · GROUP" plus, when known, the size and the release name; the error of a failed
    /// download; the health notice; nothing more, so it fits a phone notification.
    /// </summary>
    public static string Body(ConnectEvent e, bool withLinks = false)
    {
        ArgumentNullException.ThrowIfNull(e);
        var lines = new List<string> { e.Summary };
        if (e.Type is ConnectEventType.Grab or ConnectEventType.Import or ConnectEventType.Upgrade or ConnectEventType.Test)
        {
            if (e.Size is { } size and > 0)
            {
                lines.Add($"Size: {ConnectEvent.FormatSize(size)}");
            }

            if (e.Release?.Indexer is { } indexer && e.Type == ConnectEventType.Grab)
            {
                lines.Add($"Indexer: {indexer}");
            }

            if (e.Media is not null && e.Release?.Title is { } release)
            {
                lines.Add($"Release: {release}");
            }
        }
        else if (e.Type == ConnectEventType.DownloadFailure && e.Media is not null && e.Release?.Title is { } failed)
        {
            lines.Add($"Release: {failed}");
        }

        if (withLinks && Links(e) is { Count: > 0 } links)
        {
            lines.Add(string.Join(" · ", links.Select(l => l.Url)));
        }

        return string.Join("\n", lines);
    }

    public static List<(string Name, string Url)> Links(ConnectEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var links = new List<(string, string)>();
        if (e.TmdbUrl is { } tmdb)
        {
            links.Add(("TMDB", tmdb));
        }

        if (e.ImdbUrl is { } imdb)
        {
            links.Add(("IMDb", imdb));
        }

        if (e.Media?.TvdbId is { } tvdb)
        {
            links.Add(("TVDB", $"https://www.thetvdb.com/?tab=series&id={tvdb.ToString(CultureInfo.InvariantCulture)}"));
        }

        return links;
    }

    public static string Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Length <= max ? text : text[..(max - 1)] + "…";
}

/// <summary>Discord webhook with an embed (poster, quality, group, size, links …; the fields are configurable).</summary>
public sealed class DiscordSender(IHttpClientFactory httpFactory) : IConnectionSender
{
    public string Implementation => ConnectCatalog.Discord;

    public Task SendAsync(ConnectionContext context, ConnectEvent connectEvent, CancellationToken cancellationToken) =>
        PostAsync(context, Payload(context, connectEvent), cancellationToken);

    public async Task<string> TestAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
        await PostAsync(context, Payload(context, ConnectEvent.Test()), cancellationToken);
        return "Test message sent to Discord.";
    }

    public static JsonObject Payload(ConnectionContext context, ConnectEvent e)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(e);
        var settings = context.Settings;
        var fields = e.Type switch
        {
            ConnectEventType.Grab => settings.List("grabFields"),
            ConnectEventType.Import or ConnectEventType.Upgrade or ConnectEventType.Test => settings.List("importFields"),
            _ => [],
        };

        var embed = new JsonObject
        {
            ["author"] = new JsonObject { ["name"] = e.Subject },
            ["color"] = Color(e),
            ["timestamp"] = e.Time.ToString("O", CultureInfo.InvariantCulture),
        };

        if (e.Type is ConnectEventType.HealthIssue or ConnectEventType.HealthRestored)
        {
            embed["title"] = ConnectText.Truncate(e.Health?.Title, 256);
            embed["description"] = ConnectText.Truncate(e.Health?.Message, 4096);
        }
        else
        {
            embed["title"] = ConnectText.Truncate(Join(e.Media?.Display ?? e.Release?.Title ?? e.Folder, e.EpisodeText), 256);
            if (e.TmdbUrl is { } url)
            {
                embed["url"] = url;
            }

            var description = e.Type switch
            {
                ConnectEventType.DownloadFailure => e.Message ?? "Download failed",
                ConnectEventType.Rename => e.Folder,
                _ when fields.Contains("overview") => ConnectText.Truncate(e.Media?.Overview, 300),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(description))
            {
                embed["description"] = ConnectText.Truncate(description, 4096);
            }
        }

        var list = new JsonArray();
        void Add(string name, string? value, bool inline = true)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                list.Add(new JsonObject { ["name"] = name, ["value"] = ConnectText.Truncate(value, 1024), ["inline"] = inline });
            }
        }

        if (fields.Contains("quality"))
        {
            Add("Quality", e.Release?.Quality);
        }

        if (fields.Contains("group"))
        {
            Add("Group", e.Release?.ReleaseGroup);
        }

        if (fields.Contains("size") && e.Size is { } size and > 0)
        {
            Add("Size", ConnectEvent.FormatSize(size));
        }

        if (fields.Contains("indexer"))
        {
            Add("Indexer", e.Release?.Indexer);
        }

        if (fields.Contains("genres") && e.Media is { Genres.Count: > 0 } media)
        {
            Add("Genres", string.Join(", ", media.Genres.Take(5)));
        }

        if (fields.Contains("release") || e.Type == ConnectEventType.DownloadFailure)
        {
            Add("Release", e.Release?.Title, inline: false);
        }

        if (fields.Contains("links") && ConnectText.Links(e) is { Count: > 0 } links)
        {
            Add("Links", string.Join(" / ", links.Select(l => $"[{l.Name}]({l.Url})")), inline: false);
        }

        if (list.Count > 0)
        {
            embed["fields"] = list;
        }

        if (fields.Contains("poster") && e.Media?.PosterUrl is { } poster)
        {
            embed["thumbnail"] = new JsonObject { ["url"] = poster };
        }

        if (fields.Contains("fanart") && e.Media?.FanartUrl is { } fanart)
        {
            embed["image"] = new JsonObject { ["url"] = fanart };
        }

        var payload = new JsonObject
        {
            ["username"] = settings.Text("username") ?? AppInfo.Name,
            ["embeds"] = new JsonArray(embed),
        };
        if (settings.Text("avatar") is { } avatar)
        {
            payload["avatar_url"] = avatar;
        }

        return payload;
    }

    private static int Color(ConnectEvent e) => e.Type switch
    {
        ConnectEventType.Grab => 0xFFA500,
        ConnectEventType.Import => 0x27C24C,
        ConnectEventType.Upgrade => 0x3A80D2,
        ConnectEventType.Rename => 0x8E44AD,
        ConnectEventType.DownloadFailure => 0xF05050,
        ConnectEventType.HealthIssue => e.Health?.Level == "error" ? 0xF05050 : 0xFFA500,
        ConnectEventType.HealthRestored => 0x27C24C,
        _ => 0xC0202A,
    };

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private async Task PostAsync(ConnectionContext context, JsonObject payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var url = context.Settings.Text("webHookUrl") ?? throw new ConnectException("webhook URL is missing");
        var http = httpFactory.CreateClient(ConnectHttp.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        await ConnectHttp.SendAsync(http, request, cancellationToken, body => ConnectHttp.JsonProperty(body, "message"));
    }
}

/// <summary>Telegram bot message (HTML), optionally silent and into a topic.</summary>
public sealed class TelegramSender(IHttpClientFactory httpFactory) : IConnectionSender
{
    public static readonly Uri ApiBase = new("https://api.telegram.org/");

    public string Implementation => ConnectCatalog.Telegram;

    public Task SendAsync(ConnectionContext context, ConnectEvent connectEvent, CancellationToken cancellationToken) =>
        PostAsync(context, connectEvent, cancellationToken);

    public async Task<string> TestAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
        await PostAsync(context, ConnectEvent.Test(), cancellationToken);
        return "Test message sent to Telegram.";
    }

    public static string Text(ConnectEvent e, bool withLinks)
    {
        ArgumentNullException.ThrowIfNull(e);
        var text = new StringBuilder();
        text.Append("<b>").Append(Escape(e.Subject)).Append("</b>\n");
        text.Append(Escape(ConnectText.Body(e)));
        if (withLinks && ConnectText.Links(e) is { Count: > 0 } links)
        {
            text.Append('\n').Append(string.Join(" | ", links.Select(l => $"<a href=\"{Escape(l.Url)}\">{l.Name}</a>")));
        }

        return ConnectText.Truncate(text.ToString(), 4096);
    }

    /// <summary>Telegram's HTML mode only needs &amp;, &lt;, &gt; and quotes escaped (umlauts and "·" stay readable).</summary>
    public static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

    private async Task PostAsync(ConnectionContext context, ConnectEvent e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.Settings;
        var token = settings.Text("botToken") ?? throw new ConnectException("bot token is missing");
        var body = new JsonObject
        {
            ["chat_id"] = settings.Text("chatId"),
            ["text"] = Text(e, settings.Bool("includeLinks")),
            ["parse_mode"] = "HTML",
            ["disable_notification"] = settings.Bool("sendSilently"),
            ["link_preview_options"] = new JsonObject { ["is_disabled"] = true },
        };
        if (settings.Int("topicId") is { } topic)
        {
            body["message_thread_id"] = topic;
        }

        var http = httpFactory.CreateClient(ConnectHttp.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{ApiBase}bot{token}/sendMessage"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        await ConnectHttp.SendAsync(http, request, cancellationToken, b => ConnectHttp.JsonProperty(b, "description"));
    }
}

/// <summary>Pushover message with priority, sound, devices and the emergency retry/expire.</summary>
public sealed class PushoverSender(IHttpClientFactory httpFactory) : IConnectionSender
{
    public static readonly Uri MessagesUrl = new("https://api.pushover.net/1/messages.json");

    public string Implementation => ConnectCatalog.Pushover;

    public Task SendAsync(ConnectionContext context, ConnectEvent connectEvent, CancellationToken cancellationToken) =>
        PostAsync(context, connectEvent, cancellationToken);

    public async Task<string> TestAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
        await PostAsync(context, ConnectEvent.Test(), cancellationToken);
        return "Test message sent to Pushover.";
    }

    public static Dictionary<string, string> Form(ConnectionContext context, ConnectEvent e)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(e);
        var settings = context.Settings;
        var priority = settings.Text("priority") ?? "0";
        var form = new Dictionary<string, string>
        {
            ["token"] = settings.Text("apiKey") ?? string.Empty,
            ["user"] = settings.Text("userKey") ?? string.Empty,
            ["title"] = ConnectText.Truncate($"{AppInfo.Name} - {e.Subject}", 250),
            ["message"] = ConnectText.Truncate(ConnectText.Body(e), 1024),
            ["priority"] = priority,
        };
        if (settings.List("devices") is { Count: > 0 } devices)
        {
            form["device"] = string.Join(',', devices);
        }

        if (settings.Text("sound") is { } sound)
        {
            form["sound"] = sound;
        }

        if (priority == "2")
        {
            form["retry"] = (settings.Int("retry") ?? 60).ToString(CultureInfo.InvariantCulture);
            form["expire"] = (settings.Int("expire") ?? 3600).ToString(CultureInfo.InvariantCulture);
        }

        if (e.TmdbUrl is { } url)
        {
            form["url"] = url;
            form["url_title"] = "Open on TMDB";
        }

        return form;
    }

    private async Task PostAsync(ConnectionContext context, ConnectEvent e, CancellationToken cancellationToken)
    {
        var http = httpFactory.CreateClient(ConnectHttp.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, MessagesUrl) { Content = new FormUrlEncodedContent(Form(context, e)) };
        await ConnectHttp.SendAsync(http, request, cancellationToken, b => ConnectHttp.JsonProperty(b, "errors"));
    }
}

/// <summary>ntfy (JSON publish to the server root), one message per topic, token or basic authentication.</summary>
public sealed class NtfySender(IHttpClientFactory httpFactory) : IConnectionSender
{
    public const string DefaultServer = "https://ntfy.sh";

    public string Implementation => ConnectCatalog.Ntfy;

    public Task SendAsync(ConnectionContext context, ConnectEvent connectEvent, CancellationToken cancellationToken) =>
        PostAsync(context, connectEvent, cancellationToken);

    public async Task<string> TestAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
        await PostAsync(context, ConnectEvent.Test(), cancellationToken);
        return "Test message sent to ntfy.";
    }

    public static JsonObject Payload(ConnectionContext context, ConnectEvent e, string topic)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(e);
        var settings = context.Settings;
        var body = new JsonObject
        {
            ["topic"] = topic,
            ["title"] = $"{AppInfo.Name} - {e.Subject}",
            ["message"] = ConnectText.Body(e),
            ["priority"] = int.TryParse(settings.Text("priority"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 3,
        };
        if (settings.List("tags") is { Count: > 0 } tags)
        {
            body["tags"] = new JsonArray([.. tags.Select(t => (JsonNode?)JsonValue.Create(t))]);
        }

        if ((settings.Text("clickUrl") ?? e.TmdbUrl) is { } click)
        {
            body["click"] = click;
        }

        if (settings.Bool("includePoster") && e.Media?.PosterUrl is { } poster)
        {
            body["attach"] = poster;
            body["filename"] = "poster.jpg";
        }

        return body;
    }

    private async Task PostAsync(ConnectionContext context, ConnectEvent e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.Settings;
        var topics = settings.List("topics");
        if (topics.Count == 0)
        {
            throw new ConnectException("no topic set");
        }

        var server = new Uri((settings.Text("serverUrl") ?? DefaultServer).TrimEnd('/') + "/");
        var http = httpFactory.CreateClient(ConnectHttp.ClientName);
        foreach (var topic in topics)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, server)
            {
                Content = new StringContent(Payload(context, e, topic).ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (settings.Text("accessToken") is { } token)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            else if (settings.Text("username") is { } user)
            {
                request.Headers.Authorization = ConnectHttp.Basic(user, settings.Text("password"));
            }

            await ConnectHttp.SendAsync(http, request, cancellationToken, b => ConnectHttp.JsonProperty(b, "error"));
        }
    }
}

/// <summary>Gotify message with markdown and the poster as big image.</summary>
public sealed class GotifySender(IHttpClientFactory httpFactory) : IConnectionSender
{
    public string Implementation => ConnectCatalog.Gotify;

    public Task SendAsync(ConnectionContext context, ConnectEvent connectEvent, CancellationToken cancellationToken) =>
        PostAsync(context, connectEvent, cancellationToken);

    public async Task<string> TestAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
        await PostAsync(context, ConnectEvent.Test(), cancellationToken);
        return "Test message sent to Gotify.";
    }

    public static JsonObject Payload(ConnectionContext context, ConnectEvent e)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(e);
        var settings = context.Settings;
        var poster = settings.Bool("includePoster") ? e.Media?.PosterUrl : null;
        var message = ConnectText.Body(e).Replace("\n", "  \n", StringComparison.Ordinal);
        if (poster is not null)
        {
            message += $"\n\n![]({poster})";
        }

        var extras = new JsonObject { ["client::display"] = new JsonObject { ["contentType"] = "text/markdown" } };
        if (poster is not null)
        {
            extras["client::notification"] = new JsonObject { ["bigImageUrl"] = poster };
        }

        if (e.TmdbUrl is { } url)
        {
            extras["client::notification"] ??= new JsonObject();
            extras["client::notification"]!["click"] = new JsonObject { ["url"] = url };
        }

        return new JsonObject
        {
            ["title"] = $"{AppInfo.Name} - {e.Subject}",
            ["message"] = message,
            ["priority"] = int.TryParse(settings.Text("priority"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 5,
            ["extras"] = extras,
        };
    }

    private async Task PostAsync(ConnectionContext context, ConnectEvent e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var http = httpFactory.CreateClient(ConnectHttp.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ConnectHttp.BaseUri(context.Settings, "server"), "message"))
        {
            Content = new StringContent(Payload(context, e).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Gotify-Key", context.Settings.Text("appToken") ?? string.Empty);
        await ConnectHttp.SendAsync(http, request, cancellationToken, b => ConnectHttp.JsonProperty(b, "errorDescription"));
    }
}

/// <summary>Apprise API: a stored configuration (key) or stateless Apprise URLs.</summary>
public sealed class AppriseSender(IHttpClientFactory httpFactory) : IConnectionSender, IConnectionValidator
{
    public string Implementation => ConnectCatalog.Apprise;

    public void Validate(ConnectionSettings settings, Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(errors);
        if (settings.Text("configurationKey") is null && settings.Text("statelessUrls") is null)
        {
            errors.TryAdd("configurationKey", ["Enter a configuration key or Apprise URLs."]);
        }
    }

    public Task SendAsync(ConnectionContext context, ConnectEvent connectEvent, CancellationToken cancellationToken) =>
        PostAsync(context, connectEvent, cancellationToken);

    public async Task<string> TestAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
        await PostAsync(context, ConnectEvent.Test(), cancellationToken);
        return "Test message sent through Apprise.";
    }

    private async Task PostAsync(ConnectionContext context, ConnectEvent e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.Settings;
        var key = settings.Text("configurationKey");
        var urls = settings.Text("statelessUrls");
        if (key is null && urls is null)
        {
            throw new ConnectException("enter a configuration key or Apprise URLs");
        }

        var body = new JsonObject
        {
            ["title"] = $"{AppInfo.Name} - {e.Subject}",
            ["body"] = ConnectText.Body(e, withLinks: true),
            ["type"] = e.Type switch
            {
                ConnectEventType.DownloadFailure => "failure",
                ConnectEventType.HealthIssue => "warning",
                _ => settings.Text("notificationType") ?? "info",
            },
            ["format"] = "text",
        };
        if (key is null)
        {
            body["urls"] = urls;
        }

        if (settings.List("tags") is { Count: > 0 } tags)
        {
            body["tag"] = string.Join(',', tags);
        }

        var server = ConnectHttp.BaseUri(settings, "serverUrl");
        var http = httpFactory.CreateClient(ConnectHttp.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, key is null ? "notify" : $"notify/{Uri.EscapeDataString(key)}"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (settings.Text("username") is { } user)
        {
            request.Headers.Authorization = ConnectHttp.Basic(user, settings.Text("password"));
        }

        await ConnectHttp.SendAsync(http, request, cancellationToken, b => ConnectHttp.JsonProperty(b, "error") ?? ConnectHttp.Short(b));
    }
}
