// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using StreamZH.Data;

namespace StreamZH.Library.Notifications;

/// <summary>A delivery failed. The message is short ("unauthorized", "not reachable"), it becomes "Plex: unauthorized".</summary>
/// <param name="retryable">Worth another try later (network, timeout, rate limit, server error); wrong settings are not.</param>
public sealed class ConnectException(string message, bool retryable = false, Exception? inner = null) : Exception(message, inner)
{
    public bool Retryable { get; } = retryable;
}

/// <summary>Everything a sender needs for one delivery.</summary>
/// <param name="InstanceName">Settings → General → instance name (for webhooks and titles).</param>
public sealed record ConnectionContext(ConnectionDefinition Definition, ConnectionSettings Settings, string InstanceName);

/// <summary>One connection type (Plex, Discord …).</summary>
public interface IConnectionSender
{
    /// <summary>Catalog key, see <see cref="ConnectCatalog"/>.</summary>
    string Implementation { get; }

    /// <summary>Delivers the event. Only called for events the connection's triggers and type allow.</summary>
    /// <exception cref="ConnectException">The service could not be reached or refused the request.</exception>
    Task SendAsync(ConnectionContext context, ConnectEvent connectEvent, CancellationToken cancellationToken);

    /// <summary>Checks the settings (and sends a test message for notification services). Returns a short success text.</summary>
    /// <exception cref="ConnectException">The test failed.</exception>
    Task<string> TestAsync(ConnectionContext context, CancellationToken cancellationToken);
}

/// <summary>HTTP helpers shared by the senders: one named client, readable errors, JSON bodies.</summary>
public static class ConnectHttp
{
    /// <summary>Named HttpClient of all connections (tests replace its handler).</summary>
    public const string ClientName = "connect";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static StringContent JsonContent(object body, JsonSerializerOptions? options = null) =>
        new(JsonSerializer.Serialize(body, options ?? Json), Encoding.UTF8, "application/json");

    public static AuthenticationHeaderValue Basic(string username, string? password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));

    /// <summary>Sends the request and returns the body of a successful answer.</summary>
    /// <param name="errorDetail">Reads the service's own error text from an error body (null: none).</param>
    /// <exception cref="ConnectException">Network error, timeout or an error status.</exception>
    public static async Task<string> SendAsync(
        HttpClient http,
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        Func<string, string?>? errorDetail = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectException($"not reachable ({Short(ex.Message)})", retryable: true, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ConnectException("timed out", retryable: true, ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return body;
            }

            string? detail = null;
            try
            {
                detail = errorDetail?.Invoke(body);
            }
            catch (JsonException)
            {
            }

            var code = (int)response.StatusCode;
            var (message, retryable) = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => ("unauthorized", false),
                HttpStatusCode.Forbidden => ("forbidden", false),
                HttpStatusCode.NotFound => ("not found, check the URL", false),
                HttpStatusCode.TooManyRequests => ("rate limited", true),
                HttpStatusCode.RequestTimeout => ("timed out", true),
                _ when code >= 500 => ($"server error {code}", true),
                _ => ($"rejected ({code})", false),
            };
            throw new ConnectException(detail is { Length: > 0 } ? $"{message}: {Short(detail)}" : message, retryable);
        }
    }

    /// <summary>The string property <paramref name="name"/> of a JSON error body, e.g. "description" or "error".</summary>
    public static string? JsonProperty(string body, string name)
    {
        if (string.IsNullOrWhiteSpace(body) || body.TrimStart()[0] != '{')
        {
            return null;
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(v => v.ToString())),
                _ => value.ToString(),
            }
            : null;
    }

    public static string Short(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length > 160 ? line[..157] + "…" : line;
    }

    /// <summary>"http(s)://host:port/" from host, port and SSL fields.</summary>
    /// <exception cref="ConnectException">The host is not a valid host name.</exception>
    public static Uri HostUri(ConnectionSettings settings, int defaultPort)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var host = settings.Text("host") ?? "localhost";
        host = host.Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        var builder = new UriBuilder(settings.Bool("useSsl") ? Uri.UriSchemeHttps : Uri.UriSchemeHttp, host, settings.Int("port") ?? defaultPort, "/");
        try
        {
            return builder.Uri;
        }
        catch (UriFormatException ex)
        {
            throw new ConnectException($"invalid host {host}", inner: ex);
        }
    }

    /// <summary>A base URL from a URL field, always ending in "/".</summary>
    public static Uri BaseUri(ConnectionSettings settings, string field)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var text = settings.Text(field) ?? throw new ConnectException($"{field} is missing");
        return new Uri(text.EndsWith('/') ? text : text + "/");
    }
}

/// <summary>Remote path mapping for media servers that see the library under another path ("Map paths from/to").</summary>
public static class ConnectPaths
{
    /// <summary>
    /// Replaces the <paramref name="from"/> prefix of <paramref name="path"/> with <paramref name="to"/>, using the
    /// separator style of <paramref name="to"/> (backslashes for Windows servers). Unchanged when no mapping applies.
    /// </summary>
    public static string Map(string path, string? from, string? to)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrWhiteSpace(from) || to is null)
        {
            return path;
        }

        var source = Normalize(path);
        var prefix = Normalize(from).TrimEnd('/');
        if (!source.Equals(prefix, StringComparison.OrdinalIgnoreCase) && !source.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var rest = source[prefix.Length..].TrimStart('/');
        var windows = to.Contains('\\', StringComparison.Ordinal) || (to.Length >= 2 && to[1] == ':' && !to.Contains('/', StringComparison.Ordinal));
        var separator = windows ? '\\' : '/';
        var target = to.TrimEnd('/', '\\');
        if (rest.Length == 0)
        {
            return target.Length == 0 ? separator.ToString() : target;
        }

        return target + separator + (windows ? rest.Replace('/', '\\') : rest);
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it (separators and case ignored).</summary>
    public static bool IsInside(string path, string folder)
    {
        var p = Normalize(path).TrimEnd('/');
        var f = Normalize(folder).TrimEnd('/');
        return f.Length > 0 && (p.Equals(f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string path) => path.Trim().Replace('\\', '/');
}
