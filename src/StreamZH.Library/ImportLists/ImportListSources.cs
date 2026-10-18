// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Text.RegularExpressions;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library.Notifications;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Library.ImportLists;

/// <summary>Reading a list failed. The message is short ("Trakt: unauthorized"), shown on the card and in the status window.</summary>
public sealed class ImportListException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One movie or show a list returned. At least one id is set; the TMDB id is looked up when missing.</summary>
/// <param name="ReleaseDate">yyyy-MM-dd when the source knows it (TMDB); used for the minimum availability.</param>
public sealed record ImportListItem(
    MediaType MediaType,
    string Title,
    int Year,
    int? TmdbId = null,
    string? ImdbId = null,
    int? TvdbId = null,
    string? ReleaseDate = null);

/// <param name="Name">What the source calls the list (e.g. the TMDB list name), for the Test message.</param>
public sealed record ImportListFetch(IReadOnlyList<ImportListItem> Items, string? Name = null);

/// <summary>Everything a source needs to read one list.</summary>
public sealed record ImportListContext(ImportListDefinition Definition, ConnectionSettings Settings, DateTimeOffset Now)
{
    public MediaType MediaType => Definition.MediaType;

    /// <summary>The "Maximum items" field, else <paramref name="fallback"/>.</summary>
    public int Limit(int fallback) => Math.Clamp(Settings.Int("limit") ?? fallback, 1, ImportListCatalog.MaxLimit);
}

/// <summary>One import list type (TMDB, Trakt, Plex …).</summary>
public interface IImportListSource
{
    /// <summary>The catalog keys this source reads.</summary>
    IReadOnlyList<string> Implementations { get; }

    /// <exception cref="ImportListException">The list could not be read.</exception>
    Task<ImportListFetch> FetchAsync(ImportListContext context, CancellationToken cancellationToken);
}

/// <summary>Checks that go beyond the field definitions (a valid list number, a rating from 0 to 10 …).</summary>
public interface IImportListValidator
{
    void Validate(string implementation, MediaType mediaType, ConnectionSettings settings, Dictionary<string, string[]> errors);
}

/// <summary>HTTP helpers shared by the sources: one named client and readable errors.</summary>
public static partial class ImportListHttp
{
    /// <summary>Named HttpClient of all import lists (tests replace its handler).</summary>
    public const string ClientName = "importlists";

    /// <summary>Sends the request and returns the body of a successful answer.</summary>
    /// <exception cref="ImportListException">Network error, timeout or an error status.</exception>
    public static async Task<string> SendAsync(HttpClient http, HttpRequestMessage request, string service, CancellationToken cancellationToken)
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
            throw new ImportListException($"{service} is not reachable ({ex.Message.TrimEnd('.')})", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ImportListException($"{service} did not answer in time", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return body;
            }

            throw new ImportListException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => $"{service} rejected the credentials (401)",
                HttpStatusCode.Forbidden => $"{service} refused access (403)",
                HttpStatusCode.NotFound => $"{service}: list not found (404)",
                HttpStatusCode.TooManyRequests => $"{service} rate limit reached, try again later",
                _ => $"{service} answered {(int)response.StatusCode}",
            });
        }
    }

    /// <summary>"tt1160419" from any text (link, guid, CSV cell), null when there is none.</summary>
    public static string? ImdbId(string? text) => text is null ? null : ImdbRegex().Match(text) is { Success: true } m ? m.Value : null;

    /// <summary>The number of a TMDB address or a plain number: "https://www.themoviedb.org/list/8245-best" → 8245.</summary>
    public static int? TmdbNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = TmdbPathRegex().Match(text);
        var digits = match.Success ? match.Groups["id"].Value : text.Trim();
        return int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }

    [GeneratedRegex(@"tt\d{7,9}")]
    private static partial Regex ImdbRegex();

    [GeneratedRegex(@"themoviedb\.org/(?:[a-z]{2}(?:-[A-Z]{2})?/)?(?:list|collection|person|company|keyword|movie|tv)/(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TmdbPathRegex();
}

/// <summary>Maps TMDB entries to list items.</summary>
internal static class TmdbItems
{
    public static MediaType Media(TmdbMediaKind kind) => kind == TmdbMediaKind.Series ? MediaType.Series : MediaType.Movie;

    public static ImportListItem From(TmdbListEntry e) => new(Media(e.Kind), e.Title, e.Year, e.TmdbId, ReleaseDate: e.ReleaseDate);

    /// <summary>TMDB errors as list errors ("TMDB: not found").</summary>
    public static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (TmdbException ex)
        {
            throw new ImportListException(ex.StatusCode == 404 ? "TMDB: not found (check the number)" : $"TMDB: {ex.Message.TrimEnd('.')}", ex);
        }
    }
}
