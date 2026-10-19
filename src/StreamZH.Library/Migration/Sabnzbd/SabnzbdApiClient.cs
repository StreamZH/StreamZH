// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace StreamZH.Library.Migration.Sabnzbd;

/// <summary>
/// Reads SABnzbd's API: GET {url}/api?mode=…&amp;apikey=…&amp;output=json. The address may be SABnzbd itself
/// (http://host:8080) or already end in its URL base (http://host:8080/sabnzbd); when /api is not found the client tries
/// /sabnzbd/api. Read-only (version and get_config). Errors become <see cref="ValidationFailedException"/> keyed "url" or
/// "apiKey" like <see cref="ArrApiClient"/>. The API key is never logged.
/// </summary>
public sealed class SabnzbdApiClient(HttpClient http, ILogger<SabnzbdApiClient> logger)
{
    public const string UrlBase = "/sabnzbd";

    /// <summary>Checks the address and API key and finds the API (with or without "/sabnzbd"). Returns the working base url and the version.</summary>
    public async Task<(MigrationConnection Connection, MigrationAppInfo App)> ProbeAsync(string? url, string? apiKey, CancellationToken cancellationToken)
    {
        var connection = ArrApiClient.Validate(MigrationSources.Sabnzbd, url, apiKey);
        string version;
        try
        {
            version = await VersionAsync(connection, cancellationToken);
        }
        catch (NotSabnzbdException first)
        {
            if (connection.Url.EndsWith(UrlBase, StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid("url", first.Message);
            }

            var withBase = connection with { Url = connection.Url + UrlBase };
            try
            {
                version = await VersionAsync(withBase, cancellationToken);
                connection = withBase;
            }
            catch (NotSabnzbdException)
            {
                throw Invalid("url", first.Message);
            }
        }

        // "version" needs no key; reading one setting checks it.
        using (await GetCheckedAsync(connection, "get_config", cancellationToken, ("section", "misc"), ("keyword", "complete_dir")))
        {
        }

        return (connection, new MigrationAppInfo("SABnzbd", version));
    }

    /// <summary>The whole configuration (mode=get_config): misc, servers, categories, …; passwords come masked.</summary>
    public async Task<SabnzbdConfig> ConfigAsync(MigrationConnection connection, CancellationToken cancellationToken)
    {
        using var document = await GetCheckedAsync(connection, "get_config", cancellationToken);
        if (!document.RootElement.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("url", "SABnzbd answered get_config without a configuration.");
        }

        return SabnzbdConfig.Parse(config);
    }

    private async Task<string> VersionAsync(MigrationConnection connection, CancellationToken cancellationToken)
    {
        using var document = await GetAsync(connection, "version", cancellationToken);
        return document.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } version
            ? version
            : throw new NotSabnzbdException($"{new Uri(connection.Url).Authority} did not answer like SABnzbd (no version).");
    }

    private async Task<JsonDocument> GetCheckedAsync(MigrationConnection connection, string mode, CancellationToken cancellationToken, params (string Name, string Value)[] parameters)
    {
        try
        {
            return await GetAsync(connection, mode, cancellationToken, parameters);
        }
        catch (NotSabnzbdException ex)
        {
            throw Invalid("url", ex.Message);
        }
    }

    /// <summary>GET {url}/api?mode=…; the caller disposes the document.</summary>
    private async Task<JsonDocument> GetAsync(MigrationConnection connection, string mode, CancellationToken cancellationToken, params (string Name, string Value)[] parameters)
    {
        var host = new Uri(connection.Url).Authority;
        var query = $"mode={Uri.EscapeDataString(mode)}&output=json&apikey={Uri.EscapeDataString(connection.ApiKey)}"
            + string.Concat(parameters.Select(p => $"&{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(p.Value)}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{connection.Url}/api?{query}");
        request.Headers.Accept.ParseAdd("application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Invalid("url", $"SABnzbd at {host} did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogInformation("SABnzbd at {Host} is not reachable: {Message}", host, ex.Message);
            throw Invalid("url", $"Could not connect to SABnzbd at {host}: {ex.Message}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

            // SABnzbd 4.x answers a wrong key with {"status": false, "error": "API Key Incorrect"} (HTTP 200); newer
            // versions with 403 and the text; behind some proxies only the text comes through.
            if (body.Contains("API Key", StringComparison.OrdinalIgnoreCase) && ErrorText(body) is { } keyError
                && keyError.Contains("API Key", StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid("apiKey", keyError.Contains("Required", StringComparison.OrdinalIgnoreCase)
                    ? "SABnzbd wants an API key. Copy it from SABnzbd: Config > General > Security."
                    : "SABnzbd rejected the API key. Use the API key from Config > General > Security (not the NZB key).");
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw Invalid("apiKey", "SABnzbd rejected the API key (or this address is not allowed to use it). Check Config > General > Security.");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new NotSabnzbdException($"{host} answered 404 for /api. Is this SABnzbd, and does the address include its URL base?");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw Invalid("url", $"SABnzbd at {host} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            if (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSabnzbdException(
                    $"{host} answered with a web page, not with SABnzbd's API (a login page in front of it?). Use SABnzbd's own address, e.g. http://192.168.1.10:8080.");
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                throw new NotSabnzbdException($"{host} did not answer with JSON. Is this SABnzbd?");
            }

            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.False)
            {
                var error = document.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                document.Dispose();
                throw Invalid("url", $"SABnzbd answered: {error ?? "error"}.");
            }

            return document;
        }
    }

    /// <summary>The error text of an answer: "error" of a JSON object, or the plain text.</summary>
    private static string? ErrorText(string body)
    {
        var text = body.Trim();
        if (text.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                return document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // Plain text, sometimes "error: API Key Incorrect"; a long body is a page, not an error.
        return text.Length <= 200 ? text : null;
    }

    private static ValidationFailedException Invalid(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });

    /// <summary>The address answered, but not like SABnzbd's API (404, HTML, no JSON): worth trying with "/sabnzbd".</summary>
    private sealed class NotSabnzbdException(string message) : Exception(message);
}

/// <summary>SABnzbd: checks the address and key with mode=version and one get_config.</summary>
public sealed class SabnzbdMigrationProbe(SabnzbdApiClient client) : IMigrationProbe
{
    public bool Handles(string source) => source == MigrationSources.Sabnzbd;

    public Task<(MigrationConnection Connection, MigrationAppInfo App)> ProbeAsync(string source, string? url, string? apiKey, CancellationToken cancellationToken) =>
        client.ProbeAsync(url, apiKey, cancellationToken);
}
