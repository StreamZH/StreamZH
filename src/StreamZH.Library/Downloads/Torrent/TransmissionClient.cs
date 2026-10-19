// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr), Copyright (c) Radarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Downloads;

namespace StreamZH.Library.Downloads.Torrent;

/// <summary>
/// Transmission through its RPC interface (docs/rpc-spec.md), ported from Radarr's TransmissionProxy: the
/// X-Transmission-Session-Id handshake (409 → repeat with the new id), basic authentication, torrent-add with labels
/// (RPC 16, Transmission 3.00+) and download-dir, per-torrent seed limits (torrent-set), torrent-get and torrent-remove.
/// </summary>
public sealed class TransmissionClient(IHttpClientFactory httpFactory, ILogger<TransmissionClient>? logger = null) : ITorrentClient
{
    private const string Name = "Transmission";
    private const string SessionHeader = "X-Transmission-Session-Id";

    /// <summary>RPC version of Transmission 3.00, the first with labels.</summary>
    public const int LabelsRpcVersion = 16;

    private static readonly string[] Fields =
    [
        "id", "hashString", "name", "downloadDir", "totalSize", "leftUntilDone", "isFinished", "eta", "status", "secondsSeeding",
        "error", "errorString", "uploadedEver", "downloadedEver", "uploadRatio", "rateDownload", "rateUpload", "peersSendingToUs",
        "peersGettingFromUs", "addedDate", "doneDate", "labels",
    ];

    private readonly ConcurrentDictionary<string, string> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _rpcVersions = new(StringComparer.Ordinal);
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    public string Implementation => DownloadClientDefinition.Transmission;

    public async Task<TorrentClientInfo> TestAsync(DownloadClientDefinition client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var session = await CallAsync(client, "session-get", null, cancellationToken);
        var version = TorrentRequests.Str(session, "version") ?? "unknown";
        var rpc = (int)TorrentRequests.Long(session, "rpc-version");
        _rpcVersions[TorrentClientHttp.BaseUri(client).ToString()] = rpc;
        if (rpc < 14)
        {
            throw new DownloadClientException($"Transmission {version} is too old. StreamZH needs Transmission 2.80 or newer.");
        }

        if (rpc < LabelsRpcVersion && TorrentClientHttp.Setting(client, DownloadClientCatalog.Category) is not null)
        {
            throw new DownloadClientException(
                $"Transmission {version} has no labels (they came with 3.00). Clear the category or update Transmission.", DownloadClientCatalog.Category);
        }

        // "4.0.5 (a6fe2a64aa)" → "4.0.5"
        return new TorrentClientInfo(Name, version.Split(' ')[0], []);
    }

    public async Task<string> AddAsync(DownloadClientDefinition client, TorrentAddRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        var hash = TorrentRequests.Hash(request);
        var arguments = new JsonObject
        {
            ["paused"] = request.Paused || client.Flag(DownloadClientCatalog.AddPaused),
        };
        if (request.File is { } file)
        {
            arguments["metainfo"] = Convert.ToBase64String(file);
        }
        else
        {
            arguments["filename"] = request.MagnetUri;
        }

        if ((request.SavePath ?? TorrentClientHttp.Setting(client, DownloadClientCatalog.Directory)) is { } directory)
        {
            arguments["download-dir"] = directory;
        }

        if (request.Category is { } label && await RpcVersionAsync(client, cancellationToken) >= LabelsRpcVersion)
        {
            arguments["labels"] = new JsonArray(label);
        }

        var result = await CallAsync(client, "torrent-add", arguments, cancellationToken);
        var added = result.TryGetProperty("torrent-added", out var a) ? a : result.TryGetProperty("torrent-duplicate", out var d) ? d : default;
        if (added.ValueKind == JsonValueKind.Object && TorrentRequests.Str(added, "hashString") is { Length: 40 } actual)
        {
            hash = actual.ToLowerInvariant();
        }

        if (request.SeedRatio is not null || request.SeedTimeMinutes is not null)
        {
            var limits = new JsonObject { ["ids"] = new JsonArray(hash) };
            if (request.SeedRatio is { } ratio)
            {
                limits["seedRatioLimit"] = ratio;
                limits["seedRatioMode"] = 1;
            }

            // Transmission has no seed time limit; like Radarr the idle limit stands in. StreamZH checks the time itself too.
            if (request.SeedTimeMinutes is { } minutes)
            {
                limits["seedIdleLimit"] = minutes;
                limits["seedIdleMode"] = 1;
            }

            try
            {
                await CallAsync(client, "torrent-set", limits, cancellationToken);
            }
            catch (DownloadClientException ex)
            {
                _logger.LogWarning("Could not set the seed limits of torrent {Hash} in Transmission: {Message}", hash, ex.Message);
            }
        }

        return hash;
    }

    public async Task<IReadOnlyList<TorrentStatus>> GetTorrentsAsync(DownloadClientDefinition client, string? category, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var result = await CallAsync(client, "torrent-get", new JsonObject { ["fields"] = new JsonArray([.. Fields.Select(f => (JsonNode)f)]) }, cancellationToken);
        if (!result.TryGetProperty("torrents", out var torrents) || torrents.ValueKind != JsonValueKind.Array)
        {
            throw new DownloadClientException("Transmission sent an unexpected torrent list.");
        }

        var labels = category is not null && await RpcVersionAsync(client, cancellationToken) >= LabelsRpcVersion;
        return [.. torrents.EnumerateArray()
            .Where(t => !labels || HasLabel(t, category!))
            .Select(ToStatus)
            .OfType<TorrentStatus>()];
    }

    public async Task RemoveAsync(DownloadClientDefinition client, string hash, bool deleteData, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        await CallAsync(client, "torrent-remove", new JsonObject { ["ids"] = new JsonArray(hash.ToLowerInvariant()), ["delete-local-data"] = deleteData }, cancellationToken);
    }

    /// <summary>Transmission's status (0 stopped … 6 seeding) plus the remaining bytes and the error, like Radarr.</summary>
    public static TorrentState MapState(int status, long totalSize, long leftUntilDone, int error) =>
        error != 0 ? TorrentState.Error
        : status is 1 or 2 ? TorrentState.Checking
        : totalSize > 0 && leftUntilDone == 0 ? status == 0 ? TorrentState.Completed : TorrentState.Seeding
        : status switch
        {
            0 => TorrentState.Paused,
            3 => TorrentState.Queued,
            _ => TorrentState.Downloading,
        };

    private static bool HasLabel(JsonElement torrent, string label) =>
        torrent.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
        && labels.EnumerateArray().Any(l => l.ValueKind == JsonValueKind.String && string.Equals(l.GetString(), label, StringComparison.OrdinalIgnoreCase));

    private static TorrentStatus? ToStatus(JsonElement t)
    {
        if (TorrentRequests.Str(t, "hashString") is not { Length: > 0 } hash)
        {
            return null;
        }

        var size = TorrentRequests.Long(t, "totalSize");
        var left = TorrentRequests.Long(t, "leftUntilDone");
        var error = (int)TorrentRequests.Long(t, "error");
        var state = MapState((int)TorrentRequests.Long(t, "status"), size, left, error);
        var name = TorrentRequests.Str(t, "name") ?? hash;
        var folder = TorrentRequests.Str(t, "downloadDir");
        var uploaded = TorrentRequests.Long(t, "uploadedEver");
        var downloaded = TorrentRequests.Long(t, "downloadedEver");
        var ratio = downloaded > 0 ? (double)uploaded / downloaded : Math.Max(0, TorrentRequests.Double(t, "uploadRatio"));
        var done = state is TorrentState.Seeding or TorrentState.Completed;
        return new TorrentStatus(
            hash.ToLowerInvariant(),
            name,
            state,
            size,
            Math.Clamp(size - left, 0, Math.Max(0, size)),
            uploaded,
            ratio,
            TorrentRequests.Long(t, "rateDownload"),
            TorrentRequests.Long(t, "rateUpload"),
            done ? TimeSpan.Zero : TorrentClientHttp.Eta(TorrentRequests.Double(t, "eta")),
            (int)TorrentRequests.Long(t, "peersSendingToUs"),
            (int)TorrentRequests.Long(t, "peersGettingFromUs"),
            t.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array && labels.GetArrayLength() > 0 ? labels[0].GetString() : null,
            folder,
            folder is null ? null : TorrentClientHttp.Combine(folder, name),
            error != 0 ? TorrentRequests.Str(t, "errorString") is { Length: > 0 } e ? e : "Transmission reports an error for this torrent." : null,
            TorrentClientHttp.FromUnix(TorrentRequests.Long(t, "addedDate")),
            TorrentClientHttp.FromUnix(TorrentRequests.Long(t, "doneDate")),
            TimeSpan.FromSeconds(Math.Max(0, TorrentRequests.Long(t, "secondsSeeding"))));
    }

    private async Task<int> RpcVersionAsync(DownloadClientDefinition client, CancellationToken cancellationToken)
    {
        var key = TorrentClientHttp.BaseUri(client).ToString();
        if (_rpcVersions.TryGetValue(key, out var version))
        {
            return version;
        }

        var session = await CallAsync(client, "session-get", null, cancellationToken);
        version = (int)TorrentRequests.Long(session, "rpc-version");
        _rpcVersions[key] = version;
        return version;
    }

    /// <summary>One RPC call; returns its "arguments". Repeats once with the session id from a 409 answer.</summary>
    private async Task<JsonElement> CallAsync(DownloadClientDefinition client, string method, JsonObject? arguments, CancellationToken cancellationToken)
    {
        var baseUri = TorrentClientHttp.BaseUri(client);
        var http = httpFactory.CreateClient(TorrentClientHttp.HttpClientName);
        var key = baseUri.ToString();
        var payload = new JsonObject { ["method"] = method };
        if (arguments is not null)
        {
            payload["arguments"] = arguments;
        }

        var body = payload.ToJsonString();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "rpc"))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (TorrentClientHttp.Setting(client, DownloadClientCatalog.Username) is { } user)
            {
                var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{client.Text(DownloadClientCatalog.Password)}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
            }

            if (_sessions.TryGetValue(key, out var session))
            {
                request.Headers.TryAddWithoutValidation(SessionHeader, session);
            }

            using var response = await TorrentClientHttp.SendAsync(http, request, Name, cancellationToken);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Conflict when response.Headers.TryGetValues(SessionHeader, out var ids) && ids.FirstOrDefault() is { Length: > 0 } id:
                    _sessions[key] = id;
                    continue;
                case HttpStatusCode.Conflict:
                    throw new DownloadClientException("Transmission did not send a session id. Check the port and the URL base.", DownloadClientCatalog.UrlBase);
                case HttpStatusCode.Unauthorized:
                    throw new DownloadClientException("Transmission refused the login. Check the user name and password.", DownloadClientCatalog.Password);
                case HttpStatusCode.Forbidden:
                    throw new DownloadClientException("Transmission refused the connection. Add StreamZH's IP address to Transmission's RPC whitelist.", DownloadClientCatalog.Host);
                case HttpStatusCode.NotFound:
                    throw new DownloadClientException("Transmission's RPC was not found. Check the URL base (usually /transmission/).", DownloadClientCatalog.UrlBase);
                case >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest:
                    throw new DownloadClientException("Transmission redirected the request. Check the host, SSL and the URL base.", DownloadClientCatalog.UrlBase);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new DownloadClientException($"Transmission answered {TorrentClientHttp.Status(response)}.");
            }

            var text = await TorrentClientHttp.ReadAsync(response, Name, cancellationToken);
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                var result = TorrentRequests.Str(root, "result");
                if (result != "success")
                {
                    throw new DownloadClientException($"Transmission: {result ?? "unexpected answer"}");
                }

                return root.TryGetProperty("arguments", out var args) ? args.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new DownloadClientException("Transmission sent an unreadable answer. Check the port and the URL base.", DownloadClientCatalog.UrlBase, ex);
            }
        }

        throw new DownloadClientException("Transmission keeps asking for a new session id.");
    }
}
