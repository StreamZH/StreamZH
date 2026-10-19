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
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Downloads;

namespace StreamZH.Library.Downloads.Torrent;

/// <summary>
/// Deluge through the JSON-RPC of its Web UI, ported from Radarr's DelugeProxy: auth.login (cookie), connecting the Web UI
/// to the local daemon when it is not, core.add_torrent_file/magnet, seed ratio (core.set_torrent_options), labels
/// (Label plugin), web.update_ui for the torrent list and core.remove_torrent.
/// </summary>
public sealed class DelugeClient(IHttpClientFactory httpFactory, ILogger<DelugeClient>? logger = null) : ITorrentClient
{
    private const string Name = "Deluge";

    // Deluge's JSON-RPC error codes: 1 = not authenticated, 2 = unknown method (also seen while the daemon is disconnected).
    private const int NotAuthenticated = 1;
    private const int UnknownMethod = 2;

    private static readonly string[] Fields =
    [
        "hash", "name", "state", "progress", "eta", "message", "is_finished", "save_path", "total_size", "total_done", "total_uploaded",
        "time_added", "completed_time", "seeding_time", "ratio", "num_seeds", "num_peers", "download_payload_rate", "upload_payload_rate", "label",
    ];

    private readonly ConcurrentDictionary<string, string> _cookies = new(StringComparer.Ordinal);
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;
    private int _id;

    public string Implementation => DownloadClientDefinition.Deluge;

    public async Task<TorrentClientInfo> TestAsync(DownloadClientDefinition client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        string version;
        try
        {
            version = (await CallAsync(client, "daemon.get_version", [], cancellationToken)).GetString() ?? "unknown";
        }
        catch (DownloadClientException ex) when (ex.InnerException is DelugeRpcException { Code: UnknownMethod })
        {
            version = (await CallAsync(client, "daemon.info", [], cancellationToken)).GetString() ?? "1.3";
        }

        List<string> labels = [];
        var wanted = new[] { TorrentClientHttp.Setting(client, DownloadClientCatalog.Category), TorrentClientHttp.Setting(client, DownloadClientCatalog.TvCategory) }
            .OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count > 0)
        {
            if (!await LabelPluginAsync(client, cancellationToken))
            {
                throw new DownloadClientException(
                    "Deluge's Label plugin is not enabled. Enable it in Deluge (Preferences → Plugins) or clear the category.", DownloadClientCatalog.Category);
            }

            labels = await LabelsAsync(client, cancellationToken);
            foreach (var label in wanted.Where(l => !labels.Contains(l, StringComparer.Ordinal)))
            {
                await CallAsync(client, "label.add", [label], cancellationToken);
                labels.Add(label);
            }
        }

        return new TorrentClientInfo(Name, version, labels);
    }

    public async Task<string> AddAsync(DownloadClientDefinition client, TorrentAddRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        var expected = TorrentRequests.Hash(request);
        var options = new JsonObject
        {
            ["add_paused"] = request.Paused || client.Flag(DownloadClientCatalog.AddPaused),
            ["remove_at_ratio"] = false,
        };
        if ((request.SavePath ?? TorrentClientHttp.Setting(client, DownloadClientCatalog.Directory)) is { } directory)
        {
            options["download_location"] = directory;
        }

        var result = request.File is { } file
            ? await CallAsync(client, "core.add_torrent_file", [$"{expected}.torrent", Convert.ToBase64String(file), options], cancellationToken)
            : await CallAsync(client, "core.add_torrent_magnet", [request.MagnetUri, options], cancellationToken);

        // Deluge answers null for a torrent it already has.
        var hash = result.ValueKind == JsonValueKind.String && result.GetString() is { Length: 40 } added ? added.ToLowerInvariant() : expected;
        if (request.SeedRatio is { } ratio)
        {
            await TryAsync(client, "core.set_torrent_options", [new JsonArray(hash), new JsonObject { ["stop_ratio"] = ratio, ["stop_at_ratio"] = true }], hash, cancellationToken);
        }

        if (request.Category is { } label)
        {
            if (!(await LabelsAsync(client, cancellationToken)).Contains(label, StringComparer.Ordinal))
            {
                await TryAsync(client, "label.add", [label], hash, cancellationToken);
            }

            await TryAsync(client, "label.set_torrent", [hash, label], hash, cancellationToken);
        }

        return hash;
    }

    public async Task<IReadOnlyList<TorrentStatus>> GetTorrentsAsync(DownloadClientDefinition client, string? category, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var filter = new JsonObject();
        if (category is not null)
        {
            filter["label"] = category;
        }

        var result = await CallAsync(client, "web.update_ui", [new JsonArray([.. Fields.Select(f => (JsonNode)f)]), filter], cancellationToken);
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("torrents", out var torrents))
        {
            throw new DownloadClientException("Deluge sent an unexpected torrent list.");
        }

        if (result.TryGetProperty("connected", out var connected) && connected.ValueKind == JsonValueKind.False)
        {
            throw new DownloadClientException("Deluge's Web UI is not connected to the Deluge daemon.");
        }

        return torrents.ValueKind == JsonValueKind.Object
            ? [.. torrents.EnumerateObject().Select(p => ToStatus(p.Name, p.Value)).OfType<TorrentStatus>()]
            : [];
    }

    public async Task RemoveAsync(DownloadClientDefinition client, string hash, bool deleteData, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        await CallAsync(client, "core.remove_torrent", [hash.ToLowerInvariant(), deleteData], cancellationToken);
    }

    public static TorrentState MapState(string? state, bool finished) => state switch
    {
        "Error" => TorrentState.Error,
        "Paused" => finished ? TorrentState.Completed : TorrentState.Paused,
        "Checking" or "Allocating" or "Moving" => TorrentState.Checking,
        "Queued" => finished ? TorrentState.Seeding : TorrentState.Queued,
        "Seeding" => TorrentState.Seeding,
        _ => finished ? TorrentState.Seeding : TorrentState.Downloading,
    };

    private static TorrentStatus? ToStatus(string key, JsonElement t)
    {
        var hash = TorrentRequests.Str(t, "hash") ?? key;
        if (t.ValueKind != JsonValueKind.Object || hash.Length == 0 || TorrentRequests.Str(t, "name") is not { Length: > 0 } name)
        {
            return null; // Deluge sends empty entries while it reconnects to its daemon.
        }

        var finished = TorrentRequests.Bool(t, "is_finished");
        var state = MapState(TorrentRequests.Str(t, "state"), finished);
        var size = TorrentRequests.Long(t, "total_size");
        var folder = TorrentRequests.Str(t, "save_path");
        return new TorrentStatus(
            hash.ToLowerInvariant(),
            name,
            state,
            size,
            Math.Clamp(TorrentRequests.Long(t, "total_done"), 0, Math.Max(0, size)),
            TorrentRequests.Long(t, "total_uploaded"),
            Math.Max(0, TorrentRequests.Double(t, "ratio")),
            TorrentRequests.Long(t, "download_payload_rate"),
            TorrentRequests.Long(t, "upload_payload_rate"),
            finished ? TimeSpan.Zero : TorrentClientHttp.Eta(TorrentRequests.Double(t, "eta")),
            (int)TorrentRequests.Long(t, "num_seeds"),
            (int)TorrentRequests.Long(t, "num_peers"),
            TorrentRequests.Str(t, "label") is { Length: > 0 } label ? label : null,
            folder,
            folder is null ? null : TorrentClientHttp.Combine(folder, name),
            state == TorrentState.Error ? TorrentRequests.Str(t, "message") is { Length: > 0 } m ? m : "Deluge reports an error for this torrent." : null,
            TorrentClientHttp.FromUnix(TorrentRequests.Long(t, "time_added")),
            TorrentClientHttp.FromUnix(TorrentRequests.Long(t, "completed_time")),
            TimeSpan.FromSeconds(Math.Max(0, TorrentRequests.Long(t, "seeding_time"))));
    }

    private async Task<bool> LabelPluginAsync(DownloadClientDefinition client, CancellationToken cancellationToken)
    {
        var plugins = await CallAsync(client, "core.get_enabled_plugins", [], cancellationToken);
        return plugins.ValueKind == JsonValueKind.Array
            && plugins.EnumerateArray().Any(p => string.Equals(p.GetString(), "Label", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<string>> LabelsAsync(DownloadClientDefinition client, CancellationToken cancellationToken)
    {
        var labels = await CallAsync(client, "label.get_labels", [], cancellationToken);
        return labels.ValueKind == JsonValueKind.Array ? [.. labels.EnumerateArray().Select(l => l.GetString()).OfType<string>()] : [];
    }

    private async Task TryAsync(DownloadClientDefinition client, string method, JsonNode?[] parameters, string hash, CancellationToken cancellationToken)
    {
        try
        {
            await CallAsync(client, method, parameters, cancellationToken);
        }
        catch (DownloadClientException ex)
        {
            _logger.LogWarning("Deluge: {Method} for torrent {Hash} failed: {Message}", method, hash, ex.Message);
        }
    }

    /// <summary>One JSON-RPC call; logs in (and connects the daemon) when needed. Returns "result".</summary>
    private async Task<JsonElement> CallAsync(DownloadClientDefinition client, string method, JsonNode?[] parameters, CancellationToken cancellationToken)
    {
        var baseUri = TorrentClientHttp.BaseUri(client);
        var key = $"{baseUri}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.Text(DownloadClientCatalog.Password) ?? string.Empty)))}";
        var http = httpFactory.CreateClient(TorrentClientHttp.HttpClientName);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!_cookies.ContainsKey(key))
                {
                    await LoginAsync(http, baseUri, client, key, cancellationToken);
                }

                return await RawCallAsync(http, baseUri, key, method, parameters, cancellationToken);
            }
            catch (DelugeRpcException ex) when (ex.Code == NotAuthenticated && attempt == 0)
            {
                _cookies.TryRemove(key, out _);
            }
            catch (DelugeRpcException ex)
            {
                throw new DownloadClientException($"Deluge: {ex.Message}", inner: ex);
            }
        }
    }

    private async Task LoginAsync(HttpClient http, Uri baseUri, DownloadClientDefinition client, string key, CancellationToken cancellationToken)
    {
        _cookies.TryRemove(key, out _);
        var (result, cookies) = await PostAsync(http, baseUri, null, "auth.login", [client.Text(DownloadClientCatalog.Password) ?? string.Empty], cancellationToken);
        if (result.ValueKind != JsonValueKind.True || cookies.Length == 0)
        {
            throw new DownloadClientException("Deluge refused the password.", DownloadClientCatalog.Password);
        }

        _cookies[key] = cookies;
        var connected = await RawCallAsync(http, baseUri, key, "web.connected", [], cancellationToken);
        if (connected.ValueKind == JsonValueKind.True)
        {
            return;
        }

        // Like Radarr: connect the Web UI to the daemon on the same machine.
        var hosts = await RawCallAsync(http, baseUri, key, "web.get_hosts", [], cancellationToken);
        var local = hosts.ValueKind == JsonValueKind.Array
            ? hosts.EnumerateArray().FirstOrDefault(h => h.ValueKind == JsonValueKind.Array && h.GetArrayLength() > 1
                && h[1].ValueKind == JsonValueKind.String && h[1].GetString() is "127.0.0.1" or "localhost")
            : default;
        if (local.ValueKind != JsonValueKind.Array)
        {
            throw new DownloadClientException("Deluge's Web UI is not connected to a daemon and none runs on its machine. Connect it in Deluge's Connection Manager.");
        }

        await RawCallAsync(http, baseUri, key, "web.connect", [JsonNode.Parse(local[0].GetRawText())], cancellationToken);
        _logger.LogInformation("Connected Deluge's Web UI at {Address} to its daemon", baseUri.Authority);
    }

    private async Task<JsonElement> RawCallAsync(HttpClient http, Uri baseUri, string key, string method, JsonNode?[] parameters, CancellationToken cancellationToken)
    {
        var (result, _) = await PostAsync(http, baseUri, _cookies.GetValueOrDefault(key), method, parameters, cancellationToken);
        return result;
    }

    private async Task<(JsonElement Result, string Cookies)> PostAsync(
        HttpClient http, Uri baseUri, string? cookie, string method, JsonNode?[] parameters, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["method"] = method,
            ["params"] = new JsonArray([.. parameters.Select(p => p?.DeepClone())]),
            ["id"] = Interlocked.Increment(ref _id),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "json"))
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        using var response = await TorrentClientHttp.SendAsync(http, request, Name, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new DownloadClientException("Deluge's JSON-RPC was not found. Check the port (Web UI, usually 8112) and the URL base.", DownloadClientCatalog.Port);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new DownloadClientException($"Deluge answered {TorrentClientHttp.Status(response)}.");
        }

        var text = await TorrentClientHttp.ReadAsync(response, Name, cancellationToken);
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(text);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new DownloadClientException("Deluge sent an unreadable answer. Check the port and the URL base.", DownloadClientCatalog.Port, ex);
        }

        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            throw new DelugeRpcException(
                TorrentRequests.Str(error, "message") ?? "unknown error",
                (int)TorrentRequests.Long(error, "code"));
        }

        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? string.Join("; ", values.Select(v => v.Split(';', 2)[0].Trim()).Where(v => v.Contains('=', StringComparison.Ordinal)))
            : string.Empty;
        return (root.TryGetProperty("result", out var result) ? result : default, cookies);
    }

    private sealed class DelugeRpcException(string message, int code) : Exception(message)
    {
        public int Code { get; } = code;

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Deluge error {Code}: {Message}");
    }
}
