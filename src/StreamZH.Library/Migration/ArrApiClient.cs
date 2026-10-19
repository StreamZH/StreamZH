// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace StreamZH.Library.Migration;

/// <summary>
/// Reads the API of Radarr/Sonarr (/api/v3) or Prowlarr (/api/v1) with the X-Api-Key header. Read-only: StreamZH never
/// changes anything in the old app. Errors become <see cref="ValidationFailedException"/> keyed "url" or "apiKey" so
/// the wizard shows them at the right field.
/// </summary>
public sealed class ArrApiClient(HttpClient http, ILogger<ArrApiClient> logger)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The API version path of a source: v3 for Radarr and Sonarr, v1 for Prowlarr.</summary>
    public static string ApiPath(string source) => source == MigrationSources.Prowlarr ? "/api/v1" : "/api/v3";

    /// <summary>Checks url and key (shape only) and returns the normalised base url without a trailing slash.</summary>
    public static MigrationConnection Validate(string source, string? url, string? apiKey)
    {
        var (app, port) = source switch
        {
            MigrationSources.Radarr => ("Radarr", 7878),
            MigrationSources.Sonarr => ("Sonarr", 8989),
            MigrationSources.Prowlarr => ("Prowlarr", 9696),
            MigrationSources.Sabnzbd => ("SABnzbd", 8080),
            _ => (source, 80),
        };

        var errors = new Dictionary<string, string[]>();
        var baseUrl = url?.Trim().TrimEnd('/') ?? string.Empty;
        if (baseUrl.Length == 0)
        {
            errors["url"] = [$"Enter {app}'s address, e.g. http://192.168.1.10:{port}."];
        }
        else if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            errors["url"] = [$"Enter a full http:// or https:// address, e.g. http://192.168.1.10:{port}."];
        }

        var key = apiKey?.Trim() ?? string.Empty;
        if (key.Length == 0)
        {
            errors["apiKey"] = [source == MigrationSources.Sabnzbd
                ? "Enter SABnzbd's API key (Config > General > Security)."
                : $"Enter {app}'s API key (Settings > General > Security)."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        return new MigrationConnection(baseUrl, key);
    }

    /// <summary>GET {url}{apiPath}{path} as JSON. <paramref name="path"/> starts with a slash, e.g. "/qualityprofile".</summary>
    public async Task<T> GetAsync<T>(string source, MigrationConnection connection, string path, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(source, connection, path, cancellationToken);
        return document.RootElement.Deserialize<T>(Json)
            ?? throw new ValidationFailedException(new Dictionary<string, string[]> { ["url"] = [$"{Name(source)} answered {path} with an empty response."] });
    }

    /// <summary>GET as a raw JSON document (caller disposes), for shapes that differ between versions.</summary>
    public async Task<JsonDocument> GetJsonAsync(string source, MigrationConnection connection, string path, CancellationToken cancellationToken)
    {
        var host = new Uri(connection.Url).Authority;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{connection.Url}{ApiPath(source)}{path}");
        request.Headers.TryAddWithoutValidation("X-Api-Key", connection.ApiKey);
        request.Headers.Accept.ParseAdd("application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Invalid("url", $"{Name(source)} at {host} did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogInformation("{App} at {Host} is not reachable: {Message}", Name(source), host, ex.Message);
            throw Invalid("url", $"Could not connect to {Name(source)} at {host}: {ex.Message}");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw Invalid("apiKey", $"{Name(source)} rejected the API key.");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw Invalid("url", $"{host} answered 404 for {ApiPath(source)}{path}. Is this {Name(source)}, and does the address include its URL base?");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw Invalid("url", $"{Name(source)} at {host} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            try
            {
                return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            }
            catch (JsonException)
            {
                throw Invalid("url", $"{host} did not answer with JSON. Is this {Name(source)}?");
            }
        }
    }

    /// <summary>Reads /system/status and checks that the app is the expected one.</summary>
    public async Task<MigrationAppInfo> StatusAsync(string source, MigrationConnection connection, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(source, connection, "/system/status", cancellationToken);
        var root = document.RootElement;
        var appName = root.TryGetProperty("appName", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString()! : Name(source);
        var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "unknown";
        var instance = root.TryGetProperty("instanceName", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
        if (!appName.Equals(Name(source), StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("url", $"This address is {appName} {version}, not {Name(source)}.");
        }

        return new MigrationAppInfo(appName, version, instance);
    }

    public static string Name(string source) => source switch
    {
        MigrationSources.Radarr => "Radarr",
        MigrationSources.Sonarr => "Sonarr",
        MigrationSources.Prowlarr => "Prowlarr",
        MigrationSources.Sabnzbd => "SABnzbd",
        _ => source,
    };

    private static ValidationFailedException Invalid(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });
}

/// <summary>Checks the connection to a source and returns what it is. One per source; SABnzbd brings its own.</summary>
public interface IMigrationProbe
{
    bool Handles(string source);

    /// <summary>Validates url/key and asks the app who it is; throws <see cref="ValidationFailedException"/> on problems.</summary>
    Task<(MigrationConnection Connection, MigrationAppInfo App)> ProbeAsync(string source, string? url, string? apiKey, CancellationToken cancellationToken);
}

/// <summary>Radarr, Sonarr and Prowlarr: /system/status.</summary>
public sealed class ArrMigrationProbe(ArrApiClient client) : IMigrationProbe
{
    public bool Handles(string source) => source is MigrationSources.Radarr or MigrationSources.Sonarr or MigrationSources.Prowlarr;

    public async Task<(MigrationConnection Connection, MigrationAppInfo App)> ProbeAsync(
        string source, string? url, string? apiKey, CancellationToken cancellationToken)
    {
        var connection = ArrApiClient.Validate(source, url, apiKey);
        var app = await client.StatusAsync(source, connection, cancellationToken);
        return (connection, app);
    }
}
