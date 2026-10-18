// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StreamZH.Core.Tray;

namespace StreamZH.Tray.Core;

public enum ProbeOutcome
{
    Ok,

    /// <summary>StreamZH answered but refused the token (unreadable or old token, or not from this computer).</summary>
    Unauthorized,

    /// <summary>No answer: not listening (yet), or timed out.</summary>
    Unreachable,

    /// <summary>An answer that is not a status (an older StreamZH without the tray endpoints, or an error).</summary>
    Failed,
}

public sealed record ProbeResult(ProbeOutcome Outcome, TrayStatus? Status = null, string? Detail = null);

/// <summary>The HTTP calls of the tray to /api/v1/tray on this computer.</summary>
public sealed class TrayClient(HttpClient http) : IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A client for loopback: short timeout, no proxy, HTTPS certificates of 127.0.0.1/localhost accepted as they are.</summary>
    public static TrayClient Create()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None || (request.RequestUri is { } uri && IsLoopback(uri)),
        };
        return new TrayClient(new HttpClient(handler) { Timeout = Timeout });
    }

    public static bool IsLoopback(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsLoopback || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
    }

    public async Task<ProbeResult> StatusAsync(TrayConnection connection, string? run, long? after, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var query = run is null ? string.Empty : $"?run={Uri.EscapeDataString(run)}&after={(after ?? 0).ToString(CultureInfo.InvariantCulture)}";
        try
        {
            using var request = Request(HttpMethod.Get, connection, "/status" + query);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new ProbeResult(ProbeOutcome.Unauthorized);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ProbeResult(ProbeOutcome.Failed, Detail: $"HTTP {(int)response.StatusCode}");
            }

            var status = await response.Content.ReadFromJsonAsync<TrayStatus>(Json, cancellationToken).ConfigureAwait(false);
            return status is null ? new ProbeResult(ProbeOutcome.Failed, Detail: "empty answer") : new ProbeResult(ProbeOutcome.Ok, status);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new ProbeResult(ProbeOutcome.Unreachable, Detail: ex.Message);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return new ProbeResult(ProbeOutcome.Failed, Detail: ex.Message);
        }
    }

    public Task<bool> PauseAsync(TrayConnection connection) => PostAsync(connection, "/pause");

    public Task<bool> ResumeAsync(TrayConnection connection) => PostAsync(connection, "/resume");

    public Task<bool> RestartAsync(TrayConnection connection) => PostAsync(connection, "/restart");

    public Task<bool> ShutdownAsync(TrayConnection connection) => PostAsync(connection, "/shutdown");

    public void Dispose() => http.Dispose();

    /// <summary>True when StreamZH accepted the command.</summary>
    private async Task<bool> PostAsync(TrayConnection connection, string path)
    {
        ArgumentNullException.ThrowIfNull(connection);
        try
        {
            using var request = Request(HttpMethod.Post, connection, path);
            using var response = await http.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, TrayConnection connection, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(connection.Endpoint.Url.TrimEnd('/') + TrayContract.ApiPath + path));
        if (connection.Token is { } token)
        {
            request.Headers.TryAddWithoutValidation(TrayContract.Header, token);
        }

        return request;
    }
}
