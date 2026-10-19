// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr), Copyright (c) Radarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Downloads;

namespace StreamZH.Library.Downloads.Torrent;

/// <summary>
/// qBittorrent through its WebUI API v2 (qBittorrent 4.1 and newer, including 5.x), ported from Radarr's QBittorrentProxyV2:
/// cookie login (re-login on 403), add with category, save path, initial state and share limits, torrent list per category,
/// delete. The session cookie is kept per client address and login.
/// </summary>
public sealed class QBittorrentClient(IHttpClientFactory httpFactory, ILogger<QBittorrentClient>? logger = null) : ITorrentClient
{
    private const string Name = "qBittorrent";

    // qBittorrent 4.4 (API 2.8.1) takes the share limits with the add call; 5.0 (API 2.11) renamed "paused" to "stopped".
    private static readonly Version ShareLimitsOnAdd = new(2, 8, 1);
    private static readonly Version StoppedParameter = new(2, 11, 0);

    private readonly ConcurrentDictionary<string, string> _cookies = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Version> _apiVersions = new(StringComparer.Ordinal);
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    public string Implementation => DownloadClientDefinition.QBittorrent;

    public async Task<TorrentClientInfo> TestAsync(DownloadClientDefinition client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var version = (await GetTextAsync(client, "api/v2/app/version", cancellationToken)).Trim().TrimStart('v');
        var api = await ApiVersionAsync(client, cancellationToken, refresh: true);
        if (api.Major < 2)
        {
            throw new DownloadClientException($"qBittorrent {version} is too old. StreamZH needs qBittorrent 4.1 or newer.");
        }

        var categories = await CategoriesAsync(client, cancellationToken);
        foreach (var category in WantedCategories(client).Where(c => !categories.Contains(c, StringComparer.Ordinal)))
        {
            await CreateCategoryAsync(client, category, cancellationToken);
            categories.Add(category);
        }

        return new TorrentClientInfo(Name, version, [.. categories.Order(StringComparer.OrdinalIgnoreCase)]);
    }

    public async Task<string> AddAsync(DownloadClientDefinition client, TorrentAddRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        var hash = TorrentRequests.Hash(request);
        var api = await ApiVersionAsync(client, cancellationToken);
        var category = request.Category;
        if (category is not null && !(await CategoriesAsync(client, cancellationToken)).Contains(category, StringComparer.Ordinal))
        {
            await CreateCategoryAsync(client, category, cancellationToken);
        }

        var shareLimits = request.SeedRatio is not null || request.SeedTimeMinutes is not null;
        var limitsOnAdd = shareLimits && api >= ShareLimitsOnAdd;
        var stoppedName = api >= StoppedParameter ? "stopped" : "paused";
        MultipartFormDataContent Form()
        {
            var form = new MultipartFormDataContent();
            if (request.File is { } file)
            {
                var content = new ByteArrayContent(file);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-bittorrent");
                form.Add(content, "torrents", $"{hash}.torrent");
            }
            else
            {
                form.Add(new StringContent(request.MagnetUri!), "urls");
            }

            if (category is not null)
            {
                form.Add(new StringContent(category), "category");
            }

            if ((request.SavePath ?? TorrentClientHttp.Setting(client, DownloadClientCatalog.Directory)) is { } savePath)
            {
                form.Add(new StringContent(savePath), "savepath");
                form.Add(new StringContent("false"), "autoTMM");
            }

            if (request.Paused || client.Flag(DownloadClientCatalog.AddPaused))
            {
                form.Add(new StringContent("true"), stoppedName);
            }

            if (limitsOnAdd)
            {
                AddShareLimits(form, request);
            }

            return form;
        }

        var result = await SendAsync(client, () => new HttpRequestMessage(HttpMethod.Post, "api/v2/torrents/add") { Content = Form() }, cancellationToken);

        // Older versions answer with nothing, newer with "Ok."; "Fails." means qBittorrent did not take it (invalid or a duplicate).
        if (result.Trim() == "Fails.")
        {
            if (await HasTorrentAsync(client, hash, cancellationToken))
            {
                _logger.LogInformation("qBittorrent already has torrent {Hash}; StreamZH follows the existing one", hash);
                return hash;
            }

            throw new DownloadClientException("qBittorrent did not accept the torrent.");
        }

        if (shareLimits && !limitsOnAdd)
        {
            await SetShareLimitsAsync(client, hash, request, cancellationToken);
        }

        return hash;
    }

    public async Task<IReadOnlyList<TorrentStatus>> GetTorrentsAsync(DownloadClientDefinition client, string? category, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var path = category is null ? "api/v2/torrents/info" : $"api/v2/torrents/info?category={Uri.EscapeDataString(category)}";
        var json = await GetTextAsync(client, path, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new DownloadClientException("qBittorrent sent an unexpected torrent list.");
            }

            return [.. document.RootElement.EnumerateArray().Select(ToStatus).OfType<TorrentStatus>()];
        }
        catch (JsonException ex)
        {
            throw new DownloadClientException("qBittorrent sent an unreadable torrent list.", inner: ex);
        }
    }

    public async Task RemoveAsync(DownloadClientDefinition client, string hash, bool deleteData, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        await PostFormAsync(client, "api/v2/torrents/delete", [new("hashes", hash.ToLowerInvariant()), new("deleteFiles", deleteData ? "true" : "false")], cancellationToken);
    }

    /// <summary>Maps qBittorrent's states (4.x "pausedUP", 5.x "stoppedUP", …) like Radarr does.</summary>
    public static TorrentState MapState(string? state) => state switch
    {
        "error" or "missingFiles" => TorrentState.Error,
        "pausedDL" or "stoppedDL" => TorrentState.Paused,
        "queuedDL" or "checkingResumeData" or "allocating" => TorrentState.Queued,
        "checkingDL" or "checkingUP" => TorrentState.Checking,
        "pausedUP" or "stoppedUP" => TorrentState.Completed,
        "uploading" or "stalledUP" or "queuedUP" or "forcedUP" => TorrentState.Seeding,
        "stalledDL" => TorrentState.Stalled,
        _ => TorrentState.Downloading, // downloading, forcedDL, metaDL, forcedMetaDL, moving and states of newer versions
    };

    private static TorrentStatus? ToStatus(JsonElement t)
    {
        if (t.ValueKind != JsonValueKind.Object || TorrentRequests.Str(t, "hash") is not { Length: > 0 } hash)
        {
            return null;
        }

        var size = TorrentRequests.Long(t, "size");
        var progress = TorrentRequests.Double(t, "progress");
        var left = t.TryGetProperty("amount_left", out _) ? TorrentRequests.Long(t, "amount_left") : (long)(size * (1 - progress));
        var state = MapState(TorrentRequests.Str(t, "state"));
        var savePath = TorrentRequests.Str(t, "save_path");
        var name = TorrentRequests.Str(t, "name") ?? hash;
        var contentPath = TorrentRequests.Str(t, "content_path");
        if (string.IsNullOrEmpty(contentPath) && savePath is not null)
        {
            contentPath = TorrentClientHttp.Combine(savePath, name);
        }

        var error = state == TorrentState.Error
            ? TorrentRequests.Str(t, "state") == "missingFiles" ? "Files are missing." : "qBittorrent reports an error for this torrent."
            : null;
        var done = state is TorrentState.Seeding or TorrentState.Completed;
        return new TorrentStatus(
            hash.ToLowerInvariant(),
            name,
            state,
            size,
            Math.Clamp(size - left, 0, size),
            TorrentRequests.Long(t, "uploaded"),
            TorrentRequests.Double(t, "ratio"),
            TorrentRequests.Long(t, "dlspeed"),
            TorrentRequests.Long(t, "upspeed"),
            done ? TimeSpan.Zero : TorrentClientHttp.Eta(TorrentRequests.Double(t, "eta")),
            (int)TorrentRequests.Long(t, "num_seeds"),
            (int)TorrentRequests.Long(t, "num_leechs"),
            TorrentRequests.Str(t, "category"),
            savePath,
            contentPath,
            error,
            TorrentClientHttp.FromUnix(TorrentRequests.Long(t, "added_on")),
            TorrentClientHttp.FromUnix(TorrentRequests.Long(t, "completion_on")),
            TimeSpan.FromSeconds(Math.Max(0, TorrentRequests.Long(t, "seeding_time"))));
    }

    private static void AddShareLimits(MultipartFormDataContent form, TorrentAddRequest request)
    {
        // -2 = the global limit of qBittorrent.
        form.Add(new StringContent(request.SeedRatio is { } ratio ? ratio.ToString("0.###", CultureInfo.InvariantCulture) : "-2"), "ratioLimit");
        form.Add(new StringContent(request.SeedTimeMinutes is { } minutes ? minutes.ToString(CultureInfo.InvariantCulture) : "-2"), "seedingTimeLimit");
    }

    private async Task SetShareLimitsAsync(DownloadClientDefinition client, string hash, TorrentAddRequest request, CancellationToken cancellationToken)
    {
        // The torrent shows up a moment after the add call.
        for (var attempt = 0; attempt < 10 && !await HasTorrentAsync(client, hash, cancellationToken); attempt++)
        {
            await Task.Delay(200, cancellationToken);
        }

        try
        {
            await PostFormAsync(
                client,
                "api/v2/torrents/setShareLimits",
                [
                    new("hashes", hash),
                    new("ratioLimit", request.SeedRatio is { } r ? r.ToString("0.###", CultureInfo.InvariantCulture) : "-2"),
                    new("seedingTimeLimit", request.SeedTimeMinutes is { } m ? m.ToString(CultureInfo.InvariantCulture) : "-2"),
                    new("inactiveSeedingTimeLimit", "-2"),
                ],
                cancellationToken);
        }
        catch (DownloadClientException ex)
        {
            _logger.LogWarning("Could not set the seed limits of torrent {Hash} in qBittorrent: {Message}", hash, ex.Message);
        }
    }

    private async Task<bool> HasTorrentAsync(DownloadClientDefinition client, string hash, CancellationToken cancellationToken)
    {
        var json = await GetTextAsync(client, $"api/v2/torrents/info?hashes={hash}", cancellationToken);
        return json.Contains(hash, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<string>> CategoriesAsync(DownloadClientDefinition client, CancellationToken cancellationToken)
    {
        var json = await GetTextAsync(client, "api/v2/torrents/categories", cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? [.. document.RootElement.EnumerateObject().Select(p => p.Name)] : [];
        }
        catch (JsonException ex)
        {
            throw new DownloadClientException("qBittorrent sent an unreadable category list.", inner: ex);
        }
    }

    private async Task CreateCategoryAsync(DownloadClientDefinition client, string category, CancellationToken cancellationToken)
    {
        try
        {
            await PostFormAsync(client, "api/v2/torrents/createCategory", [new("category", category)], cancellationToken);
            _logger.LogInformation("Created the category {Category} in qBittorrent", category);
        }
        catch (DownloadClientException ex) when (ex.Message.Contains("409", StringComparison.Ordinal))
        {
            // Created in the meantime.
        }
        catch (DownloadClientException ex) when (ex.Message.Contains("400", StringComparison.Ordinal))
        {
            throw new DownloadClientException($"qBittorrent does not accept the category name \"{category}\".", DownloadClientCatalog.Category, ex);
        }
    }

    private static IEnumerable<string> WantedCategories(DownloadClientDefinition client) =>
        new[] { TorrentClientHttp.Setting(client, DownloadClientCatalog.Category), TorrentClientHttp.Setting(client, DownloadClientCatalog.TvCategory) }
            .OfType<string>()
            .Distinct(StringComparer.Ordinal);

    private async Task<Version> ApiVersionAsync(DownloadClientDefinition client, CancellationToken cancellationToken, bool refresh = false)
    {
        var key = TorrentClientHttp.BaseUri(client).ToString();
        if (!refresh && _apiVersions.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var text = (await GetTextAsync(client, "api/v2/app/webapiVersion", cancellationToken)).Trim();
        var version = Version.TryParse(text, out var parsed) ? parsed : new Version(2, 0);
        _apiVersions[key] = version;
        return version;
    }

    private Task<string> GetTextAsync(DownloadClientDefinition client, string path, CancellationToken cancellationToken) =>
        SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get, path), cancellationToken);

    private Task<string> PostFormAsync(DownloadClientDefinition client, string path, IEnumerable<KeyValuePair<string, string>> fields, CancellationToken cancellationToken)
    {
        var list = fields.ToList();
        return SendAsync(client, () => new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(list) }, cancellationToken);
    }

    /// <summary>Sends with the session cookie; on 403 logs in (again) once and repeats the request.</summary>
    private async Task<string> SendAsync(DownloadClientDefinition client, Func<HttpRequestMessage> build, CancellationToken cancellationToken)
    {
        var baseUri = TorrentClientHttp.BaseUri(client);
        var http = httpFactory.CreateClient(TorrentClientHttp.HttpClientName);
        var key = SessionKey(baseUri, client);
        for (var attempt = 0; ; attempt++)
        {
            if (!_cookies.ContainsKey(key) && HasLogin(client))
            {
                await LoginAsync(http, baseUri, client, key, cancellationToken);
            }

            using var request = build();
            request.RequestUri = new Uri(baseUri, request.RequestUri!.OriginalString);
            Prepare(request, baseUri);
            if (_cookies.TryGetValue(key, out var cookie))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
            }

            using var response = await TorrentClientHttp.SendAsync(http, request, Name, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Forbidden && attempt == 0 && (HasLogin(client) || _cookies.ContainsKey(key)))
            {
                _cookies.TryRemove(key, out _);
                if (!HasLogin(client))
                {
                    throw new DownloadClientException("qBittorrent wants a login. Enter the Web UI user name and password.", DownloadClientCatalog.Username);
                }

                // The session expired (or qBittorrent restarted): log in again and repeat with a fresh request.
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new DownloadClientException(
                    HasLogin(client) ? "qBittorrent refused the login." : "qBittorrent wants a login. Enter the Web UI user name and password.",
                    HasLogin(client) ? DownloadClientCatalog.Password : DownloadClientCatalog.Username);
            }

            if (response.StatusCode == HttpStatusCode.NotFound && request.RequestUri.AbsolutePath.EndsWith("webapiVersion", StringComparison.Ordinal))
            {
                throw new DownloadClientException("This is not qBittorrent 4.1 or newer (no WebUI API v2 found). Check the port and the URL base.", DownloadClientCatalog.Port);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new DownloadClientException($"qBittorrent answered {TorrentClientHttp.Status(response)}.");
            }

            return await TorrentClientHttp.ReadAsync(response, Name, cancellationToken);
        }
    }

    private async Task LoginAsync(HttpClient http, Uri baseUri, DownloadClientDefinition client, string key, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/v2/auth/login"))
        {
            Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("username", client.Text(DownloadClientCatalog.Username) ?? string.Empty),
                new KeyValuePair<string, string>("password", client.Text(DownloadClientCatalog.Password) ?? string.Empty),
            ]),
        };
        Prepare(request, baseUri);
        using var response = await TorrentClientHttp.SendAsync(http, request, Name, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new DownloadClientException("qBittorrent blocked StreamZH after too many failed logins. Wait or restart qBittorrent, then check the password.", DownloadClientCatalog.Password);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new DownloadClientException("This is not qBittorrent 4.1 or newer (no WebUI API v2 found). Check the port and the URL base.", DownloadClientCatalog.Port);
        }

        var body = response.IsSuccessStatusCode ? (await TorrentClientHttp.ReadAsync(response, Name, cancellationToken)).Trim() : string.Empty;
        if (!response.IsSuccessStatusCode || body == "Fails.")
        {
            throw new DownloadClientException("qBittorrent refused the login. Check the user name and password.", DownloadClientCatalog.Password);
        }

        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? string.Join("; ", values.Select(v => v.Split(';', 2)[0].Trim()).Where(v => v.Contains('=', StringComparison.Ordinal)))
            : string.Empty;
        if (cookies.Length == 0)
        {
            throw new DownloadClientException("qBittorrent accepted the login but sent no session cookie.");
        }

        _cookies[key] = cookies;
        _logger.LogDebug("Logged in to qBittorrent at {Address}", baseUri.Authority);
    }

    /// <summary>qBittorrent's CSRF protection compares Referer/Origin with its own address.</summary>
    private static void Prepare(HttpRequestMessage request, Uri baseUri)
    {
        request.Headers.Referrer = baseUri;
        request.Headers.TryAddWithoutValidation("Origin", baseUri.GetLeftPart(UriPartial.Authority));
    }

    private static bool HasLogin(DownloadClientDefinition client) =>
        !string.IsNullOrEmpty(client.Text(DownloadClientCatalog.Username)) || !string.IsNullOrEmpty(client.Text(DownloadClientCatalog.Password));

    private static string SessionKey(Uri baseUri, DownloadClientDefinition client)
    {
        var secret = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{client.Text(DownloadClientCatalog.Username)}\n{client.Text(DownloadClientCatalog.Password)}")));
        return $"{baseUri}|{secret}";
    }
}
