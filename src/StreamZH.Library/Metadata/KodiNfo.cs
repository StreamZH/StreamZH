// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using StreamZH.Core.Media;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Library.Metadata;

/// <summary>
/// Kodi NFO files (also read by Jellyfin, Emby and Plex with the XBMCnfo agents): movie, tvshow and episodedetails,
/// in the layout Radarr and Sonarr write. Every file carries the <see cref="Marker"/> comment, so StreamZH only ever
/// moves or deletes its own files.
/// </summary>
public static class KodiNfo
{
    public const string Marker = "Created by StreamZH";

    /// <summary>Line written into URL-only NFO files instead of the marker comment (Kodi ignores it).</summary>
    public const string UrlMarker = "# " + Marker;

    public static string MovieUrl(int tmdbId) => $"https://www.themoviedb.org/movie/{tmdbId}";

    public static string SeriesUrl(int tmdbId) => $"https://www.themoviedb.org/tv/{tmdbId}";

    public static string ImdbUrl(string imdbId) => $"https://www.imdb.com/title/{imdbId}/";

    public static string TvdbUrl(int tvdbId) => $"https://www.thetvdb.com/?tab=series&id={tvdbId}";

    /// <param name="details">Extra TMDB data; null when TMDB is not set up or not reachable (the library data is used).</param>
    public static string Movie(Movie movie, TmdbMovieDetails? details, bool originalTitle, bool withCollection, bool withUrls)
    {
        ArgumentNullException.ThrowIfNull(movie);
        var root = new XElement("movie");
        root.Add(new XElement("title", originalTitle && movie.OriginalTitle.Length > 0 ? movie.OriginalTitle : movie.Title));
        root.Add(new XElement("originaltitle", movie.OriginalTitle.Length > 0 ? movie.OriginalTitle : movie.Title));
        root.Add(new XElement("sorttitle", movie.SortTitle.Length > 0 ? movie.SortTitle : SortTitles.For(movie.Title)));
        AddRating(root, details?.Rating, details?.Votes ?? 0);
        root.Add(new XElement("userrating"));
        AddText(root, "plot", movie.Overview);
        AddText(root, "outline", movie.Overview);
        AddText(root, "tagline", details?.Tagline);
        if (movie.Runtime > 0)
        {
            root.Add(new XElement("runtime", movie.Runtime.ToString(CultureInfo.InvariantCulture)));
        }

        if (TmdbImages.Url(movie.PosterPath) is { } poster)
        {
            root.Add(new XElement("thumb", new XAttribute("aspect", "poster"), poster));
        }

        if (TmdbImages.Url(movie.BackdropPath) is { } fanart)
        {
            root.Add(new XElement("fanart", new XElement("thumb", fanart)));
        }

        AddText(root, "mpaa", details?.Certification);
        root.Add(new XElement("id", movie.ImdbId ?? movie.TmdbId.ToString(CultureInfo.InvariantCulture)));
        root.Add(new XElement("tmdbid", movie.TmdbId.ToString(CultureInfo.InvariantCulture)));
        root.Add(new XElement("uniqueid", new XAttribute("type", "tmdb"), new XAttribute("default", "true"), movie.TmdbId.ToString(CultureInfo.InvariantCulture)));
        if (movie.ImdbId is { Length: > 0 } imdb)
        {
            root.Add(new XElement("uniqueid", new XAttribute("type", "imdb"), imdb));
        }

        foreach (var genre in movie.Genres)
        {
            root.Add(new XElement("genre", genre));
        }

        foreach (var country in details?.Countries ?? [])
        {
            root.Add(new XElement("country", country));
        }

        if (withCollection && details?.Collection is { } collection)
        {
            root.Add(new XElement("set", new XElement("name", collection)));
        }

        AddText(root, "premiered", details?.ReleaseDate);
        if (movie.Year > 0)
        {
            root.Add(new XElement("year", movie.Year.ToString(CultureInfo.InvariantCulture)));
        }

        foreach (var studio in details?.Studios ?? [])
        {
            root.Add(new XElement("studio", studio));
        }

        foreach (var writer in details?.Writers ?? [])
        {
            root.Add(new XElement("credits", writer));
        }

        foreach (var director in details?.Directors ?? [])
        {
            root.Add(new XElement("director", director));
        }

        AddCast(root, details?.Cast);
        var text = Serialize(root);
        if (withUrls)
        {
            text += MovieUrl(movie.TmdbId) + "\n" + (movie.ImdbId is { Length: > 0 } id ? ImdbUrl(id) + "\n" : string.Empty);
        }

        return text;
    }

    /// <summary>An NFO with only the links; Kodi's scrapers look the movie up from it.</summary>
    public static string MovieUrlOnly(Movie movie)
    {
        ArgumentNullException.ThrowIfNull(movie);
        return UrlMarker + "\n" + MovieUrl(movie.TmdbId) + "\n" + (movie.ImdbId is { Length: > 0 } id ? ImdbUrl(id) + "\n" : string.Empty);
    }

    public static string Series(Series series, TmdbSeriesDetails? details, bool originalTitle, bool withUrls)
    {
        ArgumentNullException.ThrowIfNull(series);
        var root = new XElement("tvshow");
        var title = originalTitle && series.OriginalTitle.Length > 0 ? series.OriginalTitle : series.Title;
        root.Add(new XElement("title", title));
        root.Add(new XElement("originaltitle", series.OriginalTitle.Length > 0 ? series.OriginalTitle : series.Title));
        root.Add(new XElement("showtitle", title));
        root.Add(new XElement("sorttitle", series.SortTitle.Length > 0 ? series.SortTitle : SortTitles.For(series.Title)));
        AddRating(root, details?.Rating, details?.Votes ?? 0);
        AddText(root, "plot", series.Overview);
        AddText(root, "outline", series.Overview);
        AddText(root, "mpaa", details?.Certification);
        root.Add(new XElement("id", series.TvdbId?.ToString(CultureInfo.InvariantCulture) ?? series.TmdbId.ToString(CultureInfo.InvariantCulture)));
        if (series.TvdbId is { } tvdb)
        {
            root.Add(new XElement("uniqueid", new XAttribute("type", "tvdb"), new XAttribute("default", "true"), tvdb.ToString(CultureInfo.InvariantCulture)));
            root.Add(new XElement("uniqueid", new XAttribute("type", "tmdb"), series.TmdbId.ToString(CultureInfo.InvariantCulture)));
        }
        else
        {
            root.Add(new XElement("uniqueid", new XAttribute("type", "tmdb"), new XAttribute("default", "true"), series.TmdbId.ToString(CultureInfo.InvariantCulture)));
        }

        if (series.ImdbId is { Length: > 0 } imdb)
        {
            root.Add(new XElement("uniqueid", new XAttribute("type", "imdb"), imdb));
        }

        foreach (var genre in series.Genres)
        {
            root.Add(new XElement("genre", genre));
        }

        AddText(root, "premiered", details?.FirstAirDate);
        if (series.Year > 0)
        {
            root.Add(new XElement("year", series.Year.ToString(CultureInfo.InvariantCulture)));
        }

        AddText(root, "status", series.Status);
        var studios = details?.Studios is { Count: > 0 } s ? s : series.Network is { } network ? [network] : [];
        foreach (var studio in studios)
        {
            root.Add(new XElement("studio", studio));
        }

        if (TmdbImages.Url(series.PosterPath) is { } poster)
        {
            root.Add(new XElement("thumb", new XAttribute("aspect", "poster"), poster));
        }

        foreach (var season in details?.Seasons ?? [])
        {
            if (TmdbImages.Url(season.PosterPath) is { } seasonPoster)
            {
                root.Add(new XElement(
                    "thumb",
                    new XAttribute("aspect", "poster"),
                    new XAttribute("type", "season"),
                    new XAttribute("season", season.SeasonNumber.ToString(CultureInfo.InvariantCulture)),
                    seasonPoster));
            }
        }

        if (TmdbImages.Url(series.BackdropPath) is { } fanart)
        {
            root.Add(new XElement("fanart", new XElement("thumb", fanart)));
        }

        AddCast(root, details?.Cast);
        var text = Serialize(root);
        if (withUrls)
        {
            text += SeriesUrl(series.TmdbId) + "\n" + (series.TvdbId is { } id ? TvdbUrl(id) + "\n" : string.Empty);
        }

        return text;
    }

    /// <summary>One &lt;episodedetails&gt; per episode in the file (multi-episode files get several, as Kodi expects).</summary>
    /// <param name="details">Per episode the TMDB data (still, rating, crew), when available.</param>
    public static string Episodes(Series series, IReadOnlyList<Episode> episodes, IReadOnlyDictionary<(int Season, int Episode), TmdbEpisodeDetails> details)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(details);
        var text = new StringBuilder();
        foreach (var episode in episodes.OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber))
        {
            var tmdb = details.GetValueOrDefault((episode.SeasonNumber, episode.EpisodeNumber));
            var root = new XElement("episodedetails");
            root.Add(new XElement("title", episode.Title));
            root.Add(new XElement("showtitle", series.Title));
            root.Add(new XElement("season", episode.SeasonNumber.ToString(CultureInfo.InvariantCulture)));
            root.Add(new XElement("episode", episode.EpisodeNumber.ToString(CultureInfo.InvariantCulture)));
            AddRating(root, tmdb?.Rating, tmdb?.Votes ?? 0);
            AddText(root, "plot", episode.Overview ?? tmdb?.Overview);
            AddText(root, "aired", episode.AirDate ?? tmdb?.AirDate);
            if (tmdb?.Runtime is > 0 and var runtime)
            {
                root.Add(new XElement("runtime", runtime.ToString(CultureInfo.InvariantCulture)));
            }

            if (TmdbImages.Url(tmdb?.StillPath) is { } still)
            {
                root.Add(new XElement("thumb", still));
            }

            foreach (var writer in tmdb?.Writers ?? [])
            {
                root.Add(new XElement("credits", writer));
            }

            foreach (var director in tmdb?.Directors ?? [])
            {
                root.Add(new XElement("director", director));
            }

            root.Add(new XElement("watched", "false"));
            text.Append(text.Length == 0 ? Serialize(root) : Serialize(root, declaration: false));
        }

        return text.ToString();
    }

    /// <summary>True when the file content was written by StreamZH (XML with the marker comment, or a URL-only NFO).</summary>
    public static bool IsOurs(string content) =>
        content.Contains("<!--" + Marker + "-->", StringComparison.Ordinal) || content.StartsWith(UrlMarker, StringComparison.Ordinal);

    /// <summary>Season and episode numbers of the episodes in an episode NFO written by StreamZH.</summary>
    public static IReadOnlyList<(int Season, int Episode)> ReadEpisodeNumbers(string content)
    {
        var result = new List<(int, int)>();
        try
        {
            // Several <episodedetails> roots: wrap them, after dropping the XML declaration.
            var body = content.Replace("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>", string.Empty, StringComparison.Ordinal);
            var wrapper = XElement.Parse($"<root>{body}</root>");
            foreach (var element in wrapper.Elements("episodedetails"))
            {
                if (int.TryParse(element.Element("season")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var season)
                    && int.TryParse(element.Element("episode")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                {
                    result.Add((season, number));
                }
            }
        }
        catch (XmlException)
        {
        }

        return result;
    }

    private static void AddRating(XElement root, double? rating, int votes)
    {
        if (rating is not { } value)
        {
            return;
        }

        root.Add(new XElement(
            "ratings",
            new XElement(
                "rating",
                new XAttribute("name", "themoviedb"),
                new XAttribute("max", "10"),
                new XAttribute("default", "true"),
                new XElement("value", Math.Round(value, 1).ToString("0.0", CultureInfo.InvariantCulture)),
                new XElement("votes", votes.ToString(CultureInfo.InvariantCulture)))));
    }

    private static void AddCast(XElement root, IReadOnlyList<TmdbCastMember>? cast)
    {
        foreach (var actor in cast ?? [])
        {
            var element = new XElement("actor", new XElement("name", actor.Name));
            AddText(element, "role", actor.Character);
            element.Add(new XElement("order", actor.Order.ToString(CultureInfo.InvariantCulture)));
            if (TmdbImages.Url(actor.ProfilePath) is { } thumb)
            {
                element.Add(new XElement("thumb", thumb));
            }

            root.Add(element);
        }
    }

    private static void AddText(XElement root, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            root.Add(new XElement(name, value));
        }
    }

    private static string Serialize(XElement root, bool declaration = true)
    {
        var document = new XDocument(new XComment(Marker), root);
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            OmitXmlDeclaration = true,
            Encoding = new UTF8Encoding(false),
        };
        var text = new StringBuilder();
        if (declaration)
        {
            text.Append("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n");
        }

        using (var writer = XmlWriter.Create(text, settings))
        {
            document.Save(writer);
        }

        return text.Append('\n').ToString();
    }
}
