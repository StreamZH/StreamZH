// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library.Notifications;

namespace StreamZH.Library.ImportLists;

/// <summary>A field is only shown (and checked) while another field has one of these values.</summary>
public sealed record ImportListFieldCondition(string Field, IReadOnlyList<string> Values);

/// <summary>One setting of an import list type; same field types as Settings → Connect (<see cref="ConnectFieldTypes"/>).</summary>
/// <param name="Media">Only for lists of this media type ("movie" or "series"); null = both.</param>
/// <param name="When">Only shown while another field has one of the given values.</param>
public sealed record ImportListField(
    string Name,
    string Label,
    string Type,
    string? Help = null,
    object? Default = null,
    bool Required = false,
    bool Advanced = false,
    IReadOnlyList<ConnectFieldOption>? Options = null,
    int? Min = null,
    int? Max = null,
    string? Placeholder = null,
    string? Unit = null,
    string? Media = null,
    ImportListFieldCondition? When = null)
{
    public ConnectField ToConnectField() => new(Name, Label, Type, Help, Default, Required, Advanced, Options, Min, Max, Placeholder, Unit);
}

/// <summary>A source of movies or series (TMDB list, Trakt, Plex watchlist …).</summary>
/// <param name="Group">"tmdb", "trakt", "plex" or "other": the sections of the type picker.</param>
/// <param name="MediaTypes">What a list of this type can add; a list adds one media type.</param>
public sealed record ImportListType(
    string Key,
    string Name,
    string Group,
    string Description,
    string? InfoUrl,
    IReadOnlyList<MediaType> MediaTypes,
    IReadOnlyList<ImportListField> Fields)
{
    /// <summary>The fields as Connect fields, for the shared validation and secret masking.</summary>
    public ConnectType AsConnectType() =>
        new(Key, Name, "importList", Description, InfoUrl, [], new ConnectionTriggers(), [.. Fields.Select(f => f.ToConnectField())]);
}

/// <summary>Monitor options of a list (Radarr's and Sonarr's "Monitor").</summary>
public static class ImportListMonitor
{
    public const string MovieOnly = "movieOnly";
    public const string MovieAndCollection = "movieAndCollection";
    public const string All = "all";
    public const string Future = "future";
    public const string Missing = "missing";
    public const string FirstSeason = "firstSeason";
    public const string LatestSeason = "latestSeason";
    public const string None = "none";

    public static readonly IReadOnlyList<ConnectFieldOption> MovieOptions =
    [
        new(MovieOnly, "Movie only"),
        new(MovieAndCollection, "Movie and collection"),
        new(None, "None"),
    ];

    public static readonly IReadOnlyList<ConnectFieldOption> SeriesOptions =
    [
        new(All, "All episodes"),
        new(Future, "Future episodes"),
        new(Missing, "Missing episodes"),
        new(FirstSeason, "First season"),
        new(LatestSeason, "Latest season"),
        new(None, "None"),
    ];

    public static IReadOnlyList<ConnectFieldOption> For(MediaType type) => type == MediaType.Series ? SeriesOptions : MovieOptions;

    public static string Default(MediaType type) => type == MediaType.Series ? All : MovieOnly;
}

/// <summary>When a list adds a movie (StreamZH keeps the minimum availability for grabbing under Settings → Indexers).</summary>
public static class ImportListAvailability
{
    public const string Announced = "announced";
    public const string InCinemas = "inCinemas";
    public const string Released = "released";

    public static readonly IReadOnlyList<ConnectFieldOption> Options =
    [
        new(Announced, "Announced"),
        new(InCinemas, "In cinemas"),
        new(Released, "Released"),
    ];
}

/// <summary>Every import list type with its fields.</summary>
public static class ImportListCatalog
{
    public const string TmdbList = "tmdbList";
    public const string TmdbCollection = "tmdbCollection";
    public const string TmdbPerson = "tmdbPerson";
    public const string TmdbCompany = "tmdbCompany";
    public const string TmdbKeyword = "tmdbKeyword";
    public const string TmdbPopular = "tmdbPopular";
    public const string Trakt = "traktList";
    public const string PlexWatchlist = "plexWatchlist";
    public const string StreamZh = "streamzhList";
    public const string Rss = "rssList";

    public const string MovieMedia = "movie";
    public const string SeriesMedia = "series";

    /// <summary>Most items one list may return (TMDB pages of 20, Trakt pages).</summary>
    public const int MaxLimit = 1000;

    /// <summary>TMDB movie genres (ids are stable).</summary>
    public static readonly IReadOnlyList<ConnectFieldOption> MovieGenres =
    [
        new("28", "Action"), new("12", "Adventure"), new("16", "Animation"), new("35", "Comedy"), new("80", "Crime"),
        new("99", "Documentary"), new("18", "Drama"), new("10751", "Family"), new("14", "Fantasy"), new("36", "History"),
        new("27", "Horror"), new("10402", "Music"), new("9648", "Mystery"), new("10749", "Romance"), new("878", "Science Fiction"),
        new("10770", "TV Movie"), new("53", "Thriller"), new("10752", "War"), new("37", "Western"),
    ];

    /// <summary>TMDB TV genres.</summary>
    public static readonly IReadOnlyList<ConnectFieldOption> SeriesGenres =
    [
        new("10759", "Action & Adventure"), new("16", "Animation"), new("35", "Comedy"), new("80", "Crime"), new("99", "Documentary"),
        new("18", "Drama"), new("10751", "Family"), new("10762", "Kids"), new("9648", "Mystery"), new("10763", "News"),
        new("10764", "Reality"), new("10765", "Sci-Fi & Fantasy"), new("10766", "Soap"), new("10767", "Talk"),
        new("10768", "War & Politics"), new("37", "Western"),
    ];

    /// <summary>Crew departments of a TMDB person.</summary>
    public static readonly IReadOnlyList<ConnectFieldOption> Departments =
    [
        new("Directing", "Directing"), new("Writing", "Writing"), new("Production", "Production"), new("Sound", "Sound"),
        new("Camera", "Camera"), new("Editing", "Editing"), new("Art", "Art"), new("Costume & Make-Up", "Costume & make-up"),
        new("Visual Effects", "Visual effects"), new("Lighting", "Lighting"), new("Crew", "Other crew"),
    ];

    private static readonly MediaType[] Both = [MediaType.Movie, MediaType.Series];

    private static ImportListField Limit(int defaultValue, bool advanced = true) =>
        new("limit", "Maximum items", ConnectFieldTypes.Number, "The list is read up to this many items.", defaultValue, Advanced: advanced, Min: 1, Max: MaxLimit, Unit: "items");

    private static ImportListField TmdbId(string name, string label, string help, string placeholder) =>
        new(name, label, ConnectFieldTypes.Text, help, Required: true, Placeholder: placeholder);

    public static readonly IReadOnlyList<ImportListType> All =
    [
        new(
            TmdbList,
            "TMDB list",
            "tmdb",
            "A public list on themoviedb.org, yours or anyone's.",
            "https://www.themoviedb.org/list/",
            Both,
            [
                TmdbId("listId", "List", "The list number or its address on themoviedb.org.", "8245 or https://www.themoviedb.org/list/8245"),
                Limit(500),
            ]),
        new(
            TmdbCollection,
            "TMDB collection",
            "tmdb",
            "Every movie of a collection, e.g. all Star Wars movies.",
            "https://www.themoviedb.org/collection/10",
            [MediaType.Movie],
            [TmdbId("collectionId", "Collection", "The collection number or its address on themoviedb.org.", "10 or https://www.themoviedb.org/collection/10")]),
        new(
            TmdbPerson,
            "TMDB person",
            "tmdb",
            "The movies or shows of an actor, director, writer …",
            "https://www.themoviedb.org/person/",
            Both,
            [
                TmdbId("personId", "Person", "The person's number or address on themoviedb.org.", "31 or https://www.themoviedb.org/person/31-tom-hanks"),
                new("cast", "Cast", ConnectFieldTypes.Bool, "Add what the person acted in.", true),
                new("departments", "Crew", ConnectFieldTypes.MultiSelect, "Also add what the person worked on in these departments.", (string[])[], Options: Departments),
                Limit(500),
            ]),
        new(
            TmdbCompany,
            "TMDB company",
            "tmdb",
            "Everything a studio or production company made, e.g. Pixar or A24.",
            "https://www.themoviedb.org/company/",
            Both,
            [
                TmdbId("companyId", "Company", "The company number or its address on themoviedb.org.", "41077 or https://www.themoviedb.org/company/41077-a24"),
                Limit(100, advanced: false),
            ]),
        new(
            TmdbKeyword,
            "TMDB keyword",
            "tmdb",
            "Movies or shows tagged with a keyword, e.g. time travel.",
            "https://www.themoviedb.org/keyword/",
            Both,
            [
                TmdbId("keywordId", "Keyword", "The keyword number or its address on themoviedb.org.", "4379 or https://www.themoviedb.org/keyword/4379-time-travel"),
                Limit(100, advanced: false),
            ]),
        new(
            TmdbPopular,
            "TMDB popular and top rated",
            "tmdb",
            "Popular, top rated, upcoming or now playing, with filters for votes, rating, genre, language and years.",
            "https://www.themoviedb.org/movie",
            Both,
            [
                new(
                    "movieList",
                    "List",
                    ConnectFieldTypes.Select,
                    "Upcoming: the next 3 months. Now playing: released in the last 6 weeks.",
                    "popular",
                    Options: [new("popular", "Popular"), new("topRated", "Top rated"), new("upcoming", "Upcoming"), new("nowPlaying", "Now playing")],
                    Media: MovieMedia),
                new(
                    "seriesList",
                    "List",
                    ConnectFieldTypes.Select,
                    "On the air: an episode airs in the next 7 days.",
                    "popular",
                    Options: [new("popular", "Popular"), new("topRated", "Top rated"), new("onTheAir", "On the air")],
                    Media: SeriesMedia),
                new("minVotes", "Minimum votes", ConnectFieldTypes.Number, "Skip titles with fewer votes on TMDB. Top rated uses at least 200.", Min: 0, Max: 1_000_000, Unit: "votes"),
                new("minRating", "Minimum rating", ConnectFieldTypes.Text, "TMDB user score from 0 to 10, e.g. 6.5.", Placeholder: "6.5"),
                new("movieGenres", "Genres", ConnectFieldTypes.MultiSelect, "Any of these genres. None selected: all.", (string[])[], Options: MovieGenres, Media: MovieMedia),
                new("seriesGenres", "Genres", ConnectFieldTypes.MultiSelect, "Any of these genres. None selected: all.", (string[])[], Options: SeriesGenres, Media: SeriesMedia),
                new("language", "Original language", ConnectFieldTypes.Text, "Two-letter code, e.g. en, de or ja. Empty: all.", Placeholder: "en"),
                new("yearFrom", "From year", ConnectFieldTypes.Number, Min: 1870, Max: 2200, Advanced: true),
                new("yearTo", "To year", ConnectFieldTypes.Number, Min: 1870, Max: 2200, Advanced: true),
                new("certification", "Certification", ConnectFieldTypes.List, "Age ratings, separated by commas, e.g. G, PG, PG-13. Empty: all.", Advanced: true, Placeholder: "PG, PG-13", Media: MovieMedia),
                new("certificationCountry", "Certification country", ConnectFieldTypes.Text, "Country of the age ratings above.", "US", Advanced: true, Placeholder: "US", Media: MovieMedia),
                Limit(50, advanced: false),
            ]),
        new(
            Trakt,
            "Trakt",
            "trakt",
            "A user's watchlist or public list, or what is popular or trending on Trakt.",
            "https://trakt.tv/oauth/applications",
            Both,
            [
                new(
                    "clientId",
                    "Client ID",
                    ConnectFieldTypes.Password,
                    "Create an API app on trakt.tv (Settings → Your API Apps → New Application, redirect URI urn:ietf:wg:oauth:2.0:oob) and copy its Client ID.",
                    Required: true),
                new(
                    "listKind",
                    "List",
                    ConnectFieldTypes.Select,
                    Default: "watchlist",
                    Options: [new("watchlist", "User watchlist"), new("userList", "User list"), new("popular", "Popular"), new("trending", "Trending")]),
                new("username", "User name", ConnectFieldTypes.Text, "The Trakt user. The profile must be public.", Placeholder: "username", When: new("listKind", ["watchlist", "userList"])),
                new("listName", "List name", ConnectFieldTypes.Text, "The list as in its address: trakt.tv/users/name/lists/this-part.", Placeholder: "my-list", When: new("listKind", ["userList"])),
                new("years", "Years", ConnectFieldTypes.Text, "Popular and trending only: one year or a range, e.g. 2020-2024.", Advanced: true, Placeholder: "2020-2024", When: new("listKind", ["popular", "trending"])),
                Limit(100),
            ]),
        new(
            PlexWatchlist,
            "Plex watchlist",
            "plex",
            "Your Plex watchlist: everything you add in Plex (app, TV or plex.tv) is added here too.",
            "https://support.plex.tv/articles/204059436-finding-an-authentication-token-x-plex-token/",
            Both,
            [
                new(
                    "accessToken",
                    "Plex token",
                    ConnectFieldTypes.Password,
                    "Your X-Plex-Token, the same as for Connect → Plex. In Plex Web open any item, ⋯ → Get Info → View XML; it is at the end of the address.",
                    Required: true),
            ]),
        new(
            StreamZh,
            "StreamZH",
            "other",
            "The movies or series of another StreamZH, e.g. to keep a second server in step.",
            null,
            Both,
            [
                new("url", "URL", ConnectFieldTypes.Url, "Address of the other StreamZH.", Required: true, Placeholder: "http://192.168.1.10:8787"),
                new("apiKey", "API key", ConnectFieldTypes.Password, "Settings → General → Security on the other StreamZH.", Required: true),
                new("onlyMonitored", "Only monitored", ConnectFieldTypes.Bool, "Skip what is not monitored there.", false, Advanced: true),
            ]),
        new(
            Rss,
            "RSS list",
            "other",
            "Any feed whose items carry a TMDB, IMDb or TVDB id, e.g. from a list service or your own script.",
            null,
            Both,
            [
                new("url", "Feed URL", ConnectFieldTypes.Url, "Each item needs an id in its link, guid or description (tt1160419, themoviedb.org/movie/438631 …).", Required: true, Placeholder: "https://example.com/list.rss"),
                Limit(500),
            ]),
    ];

    public static ImportListType? Find(string? key) =>
        All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Fields of the type that apply to this media type and whose condition holds.</summary>
    public static bool Applies(ImportListField field, MediaType mediaType, Func<string, string?> value)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(value);
        if (field.Media is { } media && media != (mediaType == MediaType.Series ? SeriesMedia : MovieMedia))
        {
            return false;
        }

        return field.When is not { } when || when.Values.Contains(value(when.Field) ?? string.Empty);
    }
}
