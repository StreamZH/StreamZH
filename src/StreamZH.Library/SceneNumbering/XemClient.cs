// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) its contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace StreamZH.Library.SceneNumbering;

/// <summary>One numbering of an episode in a TheXEM map ("scene" or "tvdb").</summary>
public sealed record XemNumber(int Season, int Episode, int? Absolute);

/// <summary>A TheXEM mapping: how the scene numbers an episode that TheTVDB numbers <see cref="Tvdb"/>.</summary>
public sealed record XemMapping(XemNumber Scene, XemNumber Tvdb);

/// <summary>A scene name of a series; <see cref="SeasonNumber"/> null = all seasons (TheXEM sends -1).</summary>
public sealed record XemName(string Title, int? SeasonNumber);

/// <summary>TheXEM answered with an error or something unreadable.</summary>
public sealed class XemException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// TheXEM (thexem.info) API, like Sonarr's XemProxy: <c>map/havemap</c> (TVDB ids with a mapping), <c>map/all</c> (the
/// scene numbering of one series) and <c>map/allNames</c> (scene names, with the scene season they stand for). Requests go
/// through the app's HTTP client (proxy of Settings → General).
/// </summary>
public sealed class XemClient(IHttpClientFactory httpClients)
{
    public const string HttpClientName = "thexem";

    public static readonly Uri DefaultBaseAddress = new("https://thexem.info/");

    /// <summary>TVDB ids of all series TheXEM has a mapping for.</summary>
    public async Task<IReadOnlySet<int>> GetSeriesWithMappingsAsync(CancellationToken cancellationToken)
    {
        using var document = await GetAsync("map/havemap?origin=tvdb", cancellationToken);
        var data = Data(document, allowFailure: false);
        var ids = new HashSet<int>();
        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (Int(item) is > 0 and var id)
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    /// <summary>
    /// The scene numbering of one series. TheXEM answers "failure" for a series it has no map for; that throws, so only ask
    /// for series from <see cref="GetSeriesWithMappingsAsync"/> (a glitch then keeps the numbering StreamZH has).
    /// </summary>
    public async Task<IReadOnlyList<XemMapping>> GetMappingsAsync(int tvdbId, CancellationToken cancellationToken)
    {
        using var document = await GetAsync(
            string.Create(CultureInfo.InvariantCulture, $"map/all?id={tvdbId}&origin=tvdb"), cancellationToken);
        var data = Data(document, allowFailure: false);
        var result = new List<XemMapping>();
        if (data.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && Number(item, "scene") is { } scene
                && Number(item, "tvdb") is { } tvdb)
            {
                result.Add(new XemMapping(scene, tvdb));
            }
        }

        return result;
    }

    /// <summary>Scene names of every series TheXEM knows, by TVDB id (<c>seasonNumbers=1</c>: each name with its scene season).</summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<XemName>>> GetSceneNamesAsync(CancellationToken cancellationToken)
    {
        using var document = await GetAsync("map/allNames?origin=tvdb&seasonNumbers=1", cancellationToken);
        var data = Data(document, allowFailure: false);
        var result = new Dictionary<int, IReadOnlyList<XemName>>();
        if (data.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var series in data.EnumerateObject())
        {
            if (!int.TryParse(series.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var tvdbId) || series.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var names = new List<XemName>();
            foreach (var entry in series.Value.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } plain)
                {
                    names.Add(new XemName(plain.Trim(), null));
                    continue;
                }

                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var name in entry.EnumerateObject())
                {
                    if (name.Name.Trim().Length == 0 || Int(name.Value) is not { } season)
                    {
                        continue;
                    }

                    // Sonarr's hack for Fate/Zero: its season names point at the wrong seasons.
                    if (tvdbId == 79151 && season > 1)
                    {
                        continue;
                    }

                    names.Add(new XemName(name.Name.Trim(), season < 0 ? null : season));
                }
            }

            if (names.Count > 0)
            {
                result[tvdbId] = names;
            }
        }

        return result;
    }

    private async Task<JsonDocument> GetAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        var http = httpClients.CreateClient(HttpClientName);
        var url = new Uri(http.BaseAddress ?? DefaultBaseAddress, relativeUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new XemException($"TheXEM answered {(int)response.StatusCode} {response.ReasonPhrase} for {relativeUrl}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new XemException($"TheXEM is not reachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new XemException("TheXEM did not answer in time.", ex);
        }
        catch (JsonException ex)
        {
            throw new XemException($"TheXEM sent an unreadable answer for {relativeUrl}.", ex);
        }
    }

    /// <summary>The "data" of <c>{"result": "success", "data": …, "message": …}</c>.</summary>
    private static JsonElement Data(JsonDocument document, bool allowFailure = false)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new XemException("TheXEM sent an unexpected answer.");
        }

        var result = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        if (!string.Equals(result, "success", StringComparison.OrdinalIgnoreCase) && !allowFailure)
        {
            var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            throw new XemException($"TheXEM reported a failure: {message ?? result ?? "no result"}.");
        }

        return root.TryGetProperty("data", out var data) ? data : default;
    }

    private static XemNumber? Number(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var season = value.TryGetProperty("season", out var s) ? Int(s) : null;
        var episode = value.TryGetProperty("episode", out var e) ? Int(e) : null;
        var absolute = value.TryGetProperty("absolute", out var a) ? Int(a) : null;
        return season is >= 0 && episode is >= 0 ? new XemNumber(season.Value, episode.Value, absolute is > 0 ? absolute : null) : null;
    }

    private static int? Int(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt32(out var number) => number,
        JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) => number,
        _ => null,
    };
}
