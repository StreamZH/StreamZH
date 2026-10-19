// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using StreamZH.Core.Downloads;

namespace StreamZH.Library.Downloads.Torrent;

/// <summary>What the external torrent clients share: the base URL from the settings, the HTTP client and readable errors.</summary>
public static class TorrentClientHttp
{
    /// <summary>Named HttpClient of the external clients: no cookies container (sessions are kept per client), no redirects.</summary>
    public const string HttpClientName = "StreamZH.TorrentClients";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Longest response read from a client (a torrent list of a few thousand torrents is far below).</summary>
    public const int MaxResponseBytes = 32 * 1024 * 1024;

    /// <summary>"http(s)://host:port/urlBase/" (always ending in a slash).</summary>
    /// <exception cref="DownloadClientException">Host or port are not usable.</exception>
    public static Uri BaseUri(DownloadClientDefinition client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var host = client.Text(DownloadClientCatalog.Host)?.Trim();
        if (string.IsNullOrEmpty(host) || Uri.CheckHostName(host.Trim('[', ']')) == UriHostNameType.Unknown)
        {
            throw new DownloadClientException("Enter the host name or IP address of the download client.", DownloadClientCatalog.Host);
        }

        var port = client.Number(DownloadClientCatalog.Port) ?? 0;
        if (port is < 1 or > 65535)
        {
            throw new DownloadClientException("Enter a port between 1 and 65535.", DownloadClientCatalog.Port);
        }

        var urlBase = (client.Text(DownloadClientCatalog.UrlBase) ?? string.Empty).Trim().Trim('/');
        var scheme = client.Flag(DownloadClientCatalog.UseSsl) ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
        var hostPart = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[') ? $"[{host}]" : host;
        var text = string.Create(CultureInfo.InvariantCulture, $"{scheme}://{hostPart}:{port}/{(urlBase.Length > 0 ? urlBase + "/" : string.Empty)}");
        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
            ? uri
            : throw new DownloadClientException("The host or URL base is not valid.", DownloadClientCatalog.Host);
    }

    /// <summary>Sends and turns network problems into a <see cref="DownloadClientException"/> naming the client.</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, string clientName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DownloadClientException($"{clientName} did not answer within {Timeout.TotalSeconds:0} seconds.", DownloadClientCatalog.Host);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            throw new DownloadClientException(
                $"Could not connect to {clientName}: the SSL certificate was not accepted (Settings → General → Certificate validation).", DownloadClientCatalog.UseSsl, ex);
        }
        catch (HttpRequestException ex)
        {
            var reason = ex.InnerException is SocketException socket
                ? socket.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => "connection refused (is it running, and is the port right?)",
                    SocketError.HostNotFound or SocketError.NoData => "host not found",
                    SocketError.TimedOut => "timed out",
                    _ => socket.Message,
                }
                : ex.Message;
            throw new DownloadClientException($"Could not connect to {clientName}: {reason}", DownloadClientCatalog.Host, ex);
        }
    }

    /// <summary>The body as text, at most <see cref="MaxResponseBytes"/>.</summary>
    public static async Task<string> ReadAsync(HttpResponseMessage response, string clientName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new DownloadClientException($"{clientName} sent an answer that is too large.");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>"401 Unauthorized" style text for errors.</summary>
    public static string Status(HttpResponseMessage response) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)response.StatusCode} {response.ReasonPhrase ?? response.StatusCode.ToString()}");

    public static bool IsAuthStatus(HttpStatusCode status) => status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    /// <summary>Unix seconds → time; null for 0, negative and absurd values (clients send -1 or 0 for "never").</summary>
    public static DateTimeOffset? FromUnix(long seconds) =>
        seconds is > 0 and < 32503680000 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    /// <summary>ETA in seconds → time span; null for negative, "infinite" (qBittorrent's 8640000) and absurd values.</summary>
    public static TimeSpan? Eta(double seconds) =>
        seconds is > 0 and < 8_640_000 ? TimeSpan.FromSeconds(seconds) : null;

    /// <summary>Joins a client's save path and the torrent name the way the client's operating system does.</summary>
    public static string Combine(string folder, string name)
    {
        var separator = folder.Contains('\\', StringComparison.Ordinal) && !folder.Contains('/', StringComparison.Ordinal) ? '\\' : '/';
        return folder.TrimEnd('/', '\\') + separator + name.TrimStart('/', '\\');
    }

    /// <summary>The setting, trimmed; null when empty.</summary>
    public static string? Setting(DownloadClientDefinition client, string key) =>
        client.Text(key)?.Trim() is { Length: > 0 } value ? value : null;
}
