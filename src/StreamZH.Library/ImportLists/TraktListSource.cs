// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using StreamZH.Core.Media;
using StreamZH.Library.Notifications;

namespace StreamZH.Library.ImportLists;

/// <summary>
/// Trakt (API v2 with an app's Client ID): a user's watchlist or list, or popular and trending. Only public profiles
/// and lists: StreamZH does not sign in to Trakt (no OAuth), so private lists cannot be read.
/// </summary>
public sealed partial class TraktListSource(IHttpClientFactory httpFactory) : IImportListSource, IImportListValidator
{
    public static readonly Uri BaseAddress = new("https://api.trakt.tv/");

    /// <summary>Trakt pages hold at most 100 items.</summary>
    public const int PageSize = 100;

    private const string Service = "Trakt";

    public IReadOnlyList<string> Implementations { get; } = [ImportListCatalog.Trakt];

    public void Validate(string implementation, MediaType mediaType, ConnectionSettings settings, Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(errors);
        var kind = settings.Text("listKind") ?? "watchlist";
        if (kind is "watchlist" or "userList" && settings.Text("username") is null)
        {
            errors.TryAdd("username", ["User name is required for this list."]);
        }

        if (kind == "userList" && settings.Text("listName") is null)
        {
            errors.TryAdd("listName", ["List name is required for a user list."]);
        }

        if (kind is "popular" or "trending" && settings.Text("years") is { } years && !YearsRegex().IsMatch(years))
        {
            errors.TryAdd("years", ["Use a year or a range like 2020-2024."]);
        }
    }

    public async Task<ImportListFetch> FetchAsync(ImportListContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var s = context.Settings;
        var clientId = s.Text("clientId") ?? throw new ImportListException("Trakt: the Client ID is missing");
        var type = context.MediaType == MediaType.Series ? "shows" : "movies";
        var kind = s.Text("listKind") ?? "watchlist";
        var user = Uri.EscapeDataString((s.Text("username") ?? string.Empty).ToLowerInvariant());
        var path = kind switch
        {
            "userList" => $"users/{user}/lists/{Uri.EscapeDataString(s.Text("listName") ?? string.Empty)}/items/{type}",
            "popular" => $"{type}/popular",
            "trending" => $"{type}/trending",
            _ => $"users/{user}/watchlist/{type}",
        };
        var years = kind is "popular" or "trending" && s.Text("years") is { } y ? $"&years={Uri.EscapeDataString(y)}" : string.Empty;
        var limit = context.Limit(100);
        var http = httpFactory.CreateClient(ImportListHttp.ClientName);
        var items = new List<ImportListItem>();
        for (var page = 1; items.Count < limit; page++)
        {
            var size = Math.Min(PageSize, limit - items.Count);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseAddress, $"{path}?page={page}&limit={size}{years}"));
            request.Headers.Add("trakt-api-version", "2");
            request.Headers.Add("trakt-api-key", clientId);
            request.Headers.Accept.ParseAdd("application/json");
            var body = await ImportListHttp.SendAsync(http, request, Service, cancellationToken);
            var found = Parse(body, context.MediaType);
            items.AddRange(found.Items);
            if (found.Count < size)
            {
                break;
            }
        }

        return new ImportListFetch([.. items.Take(limit)]);
    }

    /// <summary>Reads a Trakt answer: plain items (popular) or wrapped ones ({"movie": …} for watchlists, lists, trending).</summary>
    public static (IReadOnlyList<ImportListItem> Items, int Count) Parse(string body, MediaType mediaType)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new ImportListException("Trakt: the answer is not JSON", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new ImportListException("Trakt: unexpected answer");
            }

            var wrapper = mediaType == MediaType.Series ? "show" : "movie";
            var items = new List<ImportListItem>();
            var count = 0;
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                count++;
                var media = entry.TryGetProperty(wrapper, out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : entry;
                if (!media.TryGetProperty("ids", out var ids) || ids.ValueKind != JsonValueKind.Object)
                {
                    continue; // e.g. a person or an episode in a mixed list
                }

                var title = media.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : string.Empty;
                var year = media.TryGetProperty("year", out var yr) && yr.ValueKind == JsonValueKind.Number ? yr.GetInt32() : 0;
                var item = new ImportListItem(mediaType, title, year, Int(ids, "tmdb"), Text(ids, "imdb"), mediaType == MediaType.Series ? Int(ids, "tvdb") : null);
                if (item.TmdbId is not null || item.ImdbId is not null || item.TvdbId is not null)
                {
                    items.Add(item);
                }
            }

            return (items, count);
        }
    }

    private static int? Int(JsonElement ids, string name) =>
        ids.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n > 0 ? n : null;

    private static string? Text(JsonElement ids, string name) =>
        ids.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    [GeneratedRegex(@"^\d{4}(-\d{4})?$")]
    private static partial Regex YearsRegex();
}

/// <summary>
/// The Plex watchlist of the account behind the token (discover.provider.plex.tv, as Radarr and Sonarr read it).
/// Items carry tmdb://, imdb:// and tvdb:// ids.
/// </summary>
public sealed class PlexWatchlistSource(IHttpClientFactory httpFactory) : IImportListSource
{
    public static readonly Uri BaseAddress = new("https://discover.provider.plex.tv/");

    public const int PageSize = 100;

    /// <summary>Safety stop: a watchlist is read up to this many items.</summary>
    public const int MaxItems = 5000;

    private const string Service = "Plex";

    public IReadOnlyList<string> Implementations { get; } = [ImportListCatalog.PlexWatchlist];

    public async Task<ImportListFetch> FetchAsync(ImportListContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var token = context.Settings.Text("accessToken") ?? throw new ImportListException("Plex: the token is missing");
        var type = context.MediaType == MediaType.Series ? 2 : 1;
        var http = httpFactory.CreateClient(ImportListHttp.ClientName);
        var items = new List<ImportListItem>();
        for (var start = 0; start < MaxItems; start += PageSize)
        {
            var url = new Uri(
                BaseAddress,
                $"library/sections/watchlist/all?type={type}&includeGuids=1&includeFields=title,type,year,ratingKey&excludeElements=Image"
                + $"&sort=watchlistedAt:desc&X-Plex-Container-Start={start}&X-Plex-Container-Size={PageSize}");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Plex-Token", token);
            request.Headers.Add("X-Plex-Product", "StreamZH");
            request.Headers.Add("X-Plex-Client-Identifier", "streamzh-import-lists");
            request.Headers.Accept.ParseAdd("application/json");
            var body = await ImportListHttp.SendAsync(http, request, Service, cancellationToken);
            var (page, total) = Parse(body, context.MediaType);
            items.AddRange(page);
            if (page.Count == 0 || start + PageSize >= total)
            {
                break;
            }
        }

        return new ImportListFetch(items);
    }

    /// <summary>Items of one page and the total size of the watchlist.</summary>
    public static (IReadOnlyList<ImportListItem> Items, int Total) Parse(string body, MediaType mediaType)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("MediaContainer", out var container))
            {
                throw new ImportListException("Plex: unexpected answer");
            }

            var total = container.TryGetProperty("totalSize", out var size) && size.TryGetInt32(out var n) ? n : 0;
            var items = new List<ImportListItem>();
            if (container.TryGetProperty("Metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in metadata.EnumerateArray())
                {
                    int? tmdb = null, tvdb = null;
                    string? imdb = null;
                    if (m.TryGetProperty("Guid", out var guids) && guids.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var g in guids.EnumerateArray())
                        {
                            var id = g.TryGetProperty("id", out var v) ? v.GetString() ?? string.Empty : string.Empty;
                            if (id.StartsWith("tmdb://", StringComparison.Ordinal) && int.TryParse(id[7..], NumberStyles.None, CultureInfo.InvariantCulture, out var t))
                            {
                                tmdb = t;
                            }
                            else if (mediaType == MediaType.Series && id.StartsWith("tvdb://", StringComparison.Ordinal) && int.TryParse(id[7..], NumberStyles.None, CultureInfo.InvariantCulture, out var tv))
                            {
                                tvdb = tv;
                            }
                            else if (id.StartsWith("imdb://", StringComparison.Ordinal))
                            {
                                imdb = ImportListHttp.ImdbId(id);
                            }
                        }
                    }

                    var title = m.TryGetProperty("title", out var tt) ? tt.GetString() ?? string.Empty : string.Empty;
                    var year = m.TryGetProperty("year", out var y) && y.TryGetInt32(out var yy) ? yy : 0;
                    if (tmdb is not null || imdb is not null || tvdb is not null)
                    {
                        items.Add(new ImportListItem(mediaType, title, year, tmdb, imdb, tvdb));
                    }
                }
            }

            return (items, total);
        }
        catch (JsonException ex)
        {
            throw new ImportListException("Plex: the answer is not JSON", ex);
        }
    }
}
