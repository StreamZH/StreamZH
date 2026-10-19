// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using StreamZH.Core;

namespace StreamZH.Library.Discord;

/// <summary>Discord answered with an error (or could not be reached: <see cref="Status"/> null).</summary>
/// <param name="code">Discord's JSON error code, e.g. 50001 Missing Access, 50013 Missing Permissions, 50007 cannot DM.</param>
public sealed class DiscordApiException(string message, HttpStatusCode? status = null, int? code = null, Exception? inner = null)
    : Exception(message, inner)
{
    public const int MissingAccess = 50001;
    public const int CannotDmUser = 50007;
    public const int MissingPermissions = 50013;
    public const int UnknownChannel = 10003;
    public const int UnknownInteraction = 10062;

    public HttpStatusCode? Status { get; } = status;

    public int? Code { get; } = code;

    public bool Unauthorized => Status == HttpStatusCode.Unauthorized;

    /// <summary>The bot is missing a permission or access to the guild/channel.</summary>
    public bool Forbidden => Status == HttpStatusCode.Forbidden || Code is MissingAccess or MissingPermissions;
}

/// <summary>Discord's REST API as the bot uses it. Every call that needs the bot token takes it (it is never stored here).</summary>
public interface IDiscordApi
{
    Task<DiscordUser> GetCurrentUserAsync(string token, CancellationToken cancellationToken);

    Task<DiscordApplication> GetApplicationAsync(string token, CancellationToken cancellationToken);

    Task<DiscordGatewayInfo> GetGatewayAsync(string token, CancellationToken cancellationToken);

    Task<IReadOnlyList<DiscordGuild>> GetGuildsAsync(string token, CancellationToken cancellationToken);

    Task<IReadOnlyList<DiscordChannel>> GetChannelsAsync(string token, string guildId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DiscordRole>> GetRolesAsync(string token, string guildId, CancellationToken cancellationToken);

    /// <summary>Replaces the bot's commands in one guild, or the global commands when <paramref name="guildId"/> is null.</summary>
    Task RegisterCommandsAsync(string token, string applicationId, string? guildId, IReadOnlyList<DiscordCommandDefinition> commands, CancellationToken cancellationToken);

    /// <summary>The first answer to an interaction; must arrive within 3 seconds.</summary>
    Task RespondAsync(string interactionId, string interactionToken, DiscordInteractionResponse response, CancellationToken cancellationToken);

    /// <summary>Edits the deferred answer (or, for a component interaction, the message with the component).</summary>
    Task EditOriginalAsync(string applicationId, string interactionToken, DiscordMessage message, CancellationToken cancellationToken);

    Task FollowUpAsync(string applicationId, string interactionToken, DiscordMessage message, CancellationToken cancellationToken);

    /// <summary>Opens (or finds) the direct message channel with a user; returns the channel id.</summary>
    Task<string> OpenDirectMessageAsync(string token, string userId, CancellationToken cancellationToken);

    Task SendMessageAsync(string token, string channelId, DiscordMessage message, CancellationToken cancellationToken);
}

/// <summary>Base address of the REST API (tests and development point it at a stand-in).</summary>
public sealed record DiscordApiOptions(Uri BaseAddress)
{
    public static readonly Uri DefaultBaseAddress = new("https://discord.com/api/v10/");

    public static DiscordApiOptions Default { get; } = new(DefaultBaseAddress);
}

/// <summary>
/// Lean REST client for Discord's API v10. Rate limits: a 429 is retried after Discord's <c>retry_after</c> (up to
/// <see cref="MaxRateLimitRetries"/> times), and a bucket with no requests left (X-RateLimit-Remaining: 0) is waited
/// for before the next request on the same route. The token only travels in the Authorization header; interaction
/// tokens are part of webhook URLs, so this client's HTTP logging is switched off at registration.
/// </summary>
public sealed class DiscordRestClient(HttpClient http, DiscordApiOptions options, TimeProvider time) : IDiscordApi
{
    public const string HttpClientName = "discord";
    public const int MaxRateLimitRetries = 3;

    /// <summary>Longest wait for a rate limit; a longer one fails the request instead of blocking the bot.</summary>
    public static readonly TimeSpan MaxRateLimitWait = TimeSpan.FromSeconds(30);

    private static readonly string UserAgent = $"DiscordBot ({AppInfo.Name}, {AppInfo.Version})";

    private readonly ConcurrentDictionary<string, DateTimeOffset> _blockedUntil = new(StringComparer.Ordinal);

    /// <summary>Waits (tests replace it to run without sleeping).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = (wait, ct) => Task.Delay(wait, ct);

    public async Task<DiscordUser> GetCurrentUserAsync(string token, CancellationToken cancellationToken)
    {
        using var json = await SendAsync(HttpMethod.Get, "users/@me", "users/@me", token, null, cancellationToken);
        return DiscordJson.ReadUser(json.RootElement) ?? throw new DiscordApiException("Discord sent no bot user.");
    }

    public async Task<DiscordApplication> GetApplicationAsync(string token, CancellationToken cancellationToken)
    {
        using var json = await SendAsync(HttpMethod.Get, "applications/@me", "applications/@me", token, null, cancellationToken);
        var root = json.RootElement;
        return new DiscordApplication(DiscordJson.String(root, "id") ?? string.Empty, DiscordJson.String(root, "name") ?? string.Empty);
    }

    public async Task<DiscordGatewayInfo> GetGatewayAsync(string token, CancellationToken cancellationToken)
    {
        using var json = await SendAsync(HttpMethod.Get, "gateway/bot", "gateway/bot", token, null, cancellationToken);
        var root = json.RootElement;
        var remaining = 1000;
        var resetAfter = TimeSpan.Zero;
        if (root.TryGetProperty("session_start_limit", out var limit) && limit.ValueKind == JsonValueKind.Object)
        {
            remaining = limit.TryGetProperty("remaining", out var r) && r.TryGetInt32(out var rv) ? rv : remaining;
            resetAfter = limit.TryGetProperty("reset_after", out var a) && a.TryGetInt64(out var ms) ? TimeSpan.FromMilliseconds(ms) : resetAfter;
        }

        return new DiscordGatewayInfo(DiscordJson.String(root, "url") ?? "wss://gateway.discord.gg", remaining, resetAfter);
    }

    public async Task<IReadOnlyList<DiscordGuild>> GetGuildsAsync(string token, CancellationToken cancellationToken)
    {
        using var json = await SendAsync(HttpMethod.Get, "users/@me/guilds", "users/@me/guilds", token, null, cancellationToken);
        return [.. Array(json.RootElement).Select(g => new DiscordGuild(DiscordJson.String(g, "id") ?? string.Empty, DiscordJson.String(g, "name") ?? string.Empty))];
    }

    public async Task<IReadOnlyList<DiscordChannel>> GetChannelsAsync(string token, string guildId, CancellationToken cancellationToken)
    {
        using var json = await SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/channels", $"guilds/{guildId}/channels", token, null, cancellationToken);
        return
        [
            .. Array(json.RootElement).Select(c => new DiscordChannel(
                DiscordJson.String(c, "id") ?? string.Empty,
                DiscordJson.String(c, "name") ?? string.Empty,
                c.TryGetProperty("type", out var type) && type.TryGetInt32(out var t) ? t : -1,
                DiscordJson.String(c, "parent_id"))),
        ];
    }

    public async Task<IReadOnlyList<DiscordRole>> GetRolesAsync(string token, string guildId, CancellationToken cancellationToken)
    {
        using var json = await SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/roles", $"guilds/{guildId}/roles", token, null, cancellationToken);
        return
        [
            .. Array(json.RootElement).Select(r => new DiscordRole(
                DiscordJson.String(r, "id") ?? string.Empty,
                DiscordJson.String(r, "name") ?? string.Empty,
                r.TryGetProperty("managed", out var managed) && managed.ValueKind == JsonValueKind.True)),
        ];
    }

    public async Task RegisterCommandsAsync(
        string token, string applicationId, string? guildId, IReadOnlyList<DiscordCommandDefinition> commands, CancellationToken cancellationToken)
    {
        var path = guildId is null
            ? $"applications/{Id(applicationId)}/commands"
            : $"applications/{Id(applicationId)}/guilds/{Id(guildId)}/commands";
        using var _ = await SendAsync(HttpMethod.Put, path, path, token, commands, cancellationToken);
    }

    public async Task RespondAsync(string interactionId, string interactionToken, DiscordInteractionResponse response, CancellationToken cancellationToken)
    {
        using var _ = await SendAsync(
            HttpMethod.Post, $"interactions/{Id(interactionId)}/{Uri.EscapeDataString(interactionToken)}/callback", "interactions/callback", null, response, cancellationToken);
    }

    public async Task EditOriginalAsync(string applicationId, string interactionToken, DiscordMessage message, CancellationToken cancellationToken)
    {
        using var _ = await SendAsync(
            HttpMethod.Patch,
            $"webhooks/{Id(applicationId)}/{Uri.EscapeDataString(interactionToken)}/messages/@original",
            $"webhooks/{applicationId}/original",
            null,
            message,
            cancellationToken);
    }

    public async Task FollowUpAsync(string applicationId, string interactionToken, DiscordMessage message, CancellationToken cancellationToken)
    {
        using var _ = await SendAsync(
            HttpMethod.Post, $"webhooks/{Id(applicationId)}/{Uri.EscapeDataString(interactionToken)}", $"webhooks/{applicationId}", null, message, cancellationToken);
    }

    public async Task<string> OpenDirectMessageAsync(string token, string userId, CancellationToken cancellationToken)
    {
        using var json = await SendAsync(HttpMethod.Post, "users/@me/channels", "users/@me/channels", token, new { recipient_id = Id(userId) }, cancellationToken);
        return DiscordJson.String(json.RootElement, "id") ?? throw new DiscordApiException("Discord sent no channel.");
    }

    public async Task SendMessageAsync(string token, string channelId, DiscordMessage message, CancellationToken cancellationToken)
    {
        using var _ = await SendAsync(HttpMethod.Post, $"channels/{Id(channelId)}/messages", $"channels/{channelId}/messages", token, message, cancellationToken);
    }

    /// <summary>Sends one request; <paramref name="route"/> keys the rate limit bucket (Discord buckets per route and major id).</summary>
    private async Task<JsonDocument> SendAsync(
        HttpMethod method, string path, string route, string? token, object? body, CancellationToken cancellationToken)
    {
        var routeKey = $"{method.Method} {route}";
        for (var attempt = 0; ; attempt++)
        {
            if (_blockedUntil.TryGetValue(routeKey, out var until) && until - time.GetUtcNow() is { } wait && wait > TimeSpan.Zero)
            {
                await Delay(wait < MaxRateLimitWait ? wait : MaxRateLimitWait, cancellationToken);
            }

            using var request = new HttpRequestMessage(method, new Uri(options.BaseAddress, path));
            request.Headers.UserAgent.ParseAdd(UserAgent);
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
            }

            if (body is not null)
            {
                request.Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), DiscordJson.Options), Encoding.UTF8, "application/json");
            }

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new DiscordApiException("Discord could not be reached.", null, null, ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new DiscordApiException("Discord did not answer in time.", null, null, ex);
            }

            using (response)
            {
                RememberBucket(routeKey, response);
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retryAfter = RetryAfter(response, text);
                    if (attempt >= MaxRateLimitRetries || retryAfter > MaxRateLimitWait)
                    {
                        throw new DiscordApiException("Discord rate limit reached; try again in a moment.", response.StatusCode);
                    }

                    await Delay(retryAfter, cancellationToken);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw Error(response.StatusCode, text);
                }

                return JsonDocument.Parse(text.Length == 0 ? "{}" : text);
            }
        }
    }

    private void RememberBucket(string routeKey, HttpResponseMessage response)
    {
        if (Header(response, "X-RateLimit-Remaining") == "0"
            && double.TryParse(Header(response, "X-RateLimit-Reset-After"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            _blockedUntil[routeKey] = time.GetUtcNow() + TimeSpan.FromSeconds(seconds);
        }
        else
        {
            _blockedUntil.TryRemove(routeKey, out _);
        }
    }

    /// <summary>Discord's retry_after (seconds, may be fractional) from the body, else the Retry-After header, else one second.</summary>
    private static TimeSpan RetryAfter(HttpResponseMessage response, string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("retry_after", out var value) && value.TryGetDouble(out var seconds))
            {
                return TimeSpan.FromSeconds(Math.Max(0, seconds));
            }
        }
        catch (JsonException)
        {
        }

        return response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
    }

    private static DiscordApiException Error(HttpStatusCode status, string body)
    {
        int? code = null;
        string? message = null;
        try
        {
            using var json = JsonDocument.Parse(body);
            code = json.RootElement.TryGetProperty("code", out var c) && c.TryGetInt32(out var cv) ? cv : null;
            message = DiscordJson.String(json.RootElement, "message");
        }
        catch (JsonException)
        {
        }

        var text = status switch
        {
            HttpStatusCode.Unauthorized => "Discord rejected the bot token. Copy a new one from Bot → Reset Token in the Developer Portal.",
            HttpStatusCode.Forbidden => code == DiscordApiException.CannotDmUser
                ? "Discord does not let the bot send this user a direct message."
                : $"The bot is missing access or a permission ({message ?? "forbidden"}).",
            HttpStatusCode.NotFound => $"Discord could not find it ({message ?? "not found"}).",
            _ when (int)status >= 500 => "Discord has a problem right now; try again later.",
            _ => $"Discord refused the request: {message ?? status.ToString()}.",
        };
        return new DiscordApiException(text, status, code);
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static IEnumerable<JsonElement> Array(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];

    /// <summary>Ids go into URL paths: only digits pass, anything else is refused before a request is made.</summary>
    private static string Id(string id) =>
        DiscordBotSettings.IsSnowflake(id) ? id : throw new DiscordApiException("Not a Discord id.");
}
