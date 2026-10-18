// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using StreamZH.Core.CustomFormats;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Data;

namespace StreamZH.Library.Import;

/// <summary>Which naming format a text is (for validation: allowed and required tokens differ).</summary>
public enum NamingFormatKind
{
    MovieFile,
    MovieFolder,
    StandardEpisode,
    DailyEpisode,
    AnimeEpisode,
    SeriesFolder,
    SeasonFolder,
    SpecialsFolder,
}

/// <summary>Which episode format applies to a file (Sonarr's series type).</summary>
public enum EpisodeFormat
{
    Standard,
    Daily,
    Anime,
}

/// <summary>What the naming knows about one file: quality and the rest read from the release name.</summary>
public sealed record NamingFileInfo
{
    public QualityModel Quality { get; init; } = QualityModel.Unknown;
    public IReadOnlyList<Language> Languages { get; init; } = [];
    public string? ReleaseGroup { get; init; }
    public string? Edition { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public HdrFormat Hdr { get; init; }

    /// <summary>The release the file came from ({Original Title}).</summary>
    public string? SceneName { get; init; }

    /// <summary>File name without extension as downloaded ({Original Filename}).</summary>
    public string? OriginalFileName { get; init; }

    /// <summary>The custom formats the file matches, ordered by name ({Custom Formats} shows those with "include in rename").</summary>
    public IReadOnlyList<CustomFormat> CustomFormats { get; init; } = [];

    public static NamingFileInfo From(ParsedRelease? parsed, string? sceneName, string? originalFileName) => new()
    {
        Quality = parsed?.Quality ?? QualityModel.Unknown,
        Languages = parsed?.Languages ?? [],
        ReleaseGroup = parsed?.ReleaseGroup,
        Edition = parsed?.Edition,
        VideoCodec = parsed?.VideoCodec,
        AudioCodec = parsed?.AudioCodec,
        Hdr = parsed?.Hdr ?? HdrFormat.None,
        SceneName = sceneName,
        OriginalFileName = originalFileName,
    };

    public static NamingFileInfo From(MediaFileDetails details, string? originalFileName) => new()
    {
        Quality = new QualityModel(Core.Quality.Quality.FindById(details.QualityId), new Revision(details.RevisionVersion, details.RevisionReal, details.IsRepack)),
        Languages = [.. details.LanguageIds.Select(Language.FindById).Where(l => l != Language.Unknown)],
        ReleaseGroup = details.ReleaseGroup,
        Edition = details.Edition,
        VideoCodec = details.VideoCodec,
        AudioCodec = details.AudioCodec,
        SceneName = details.SceneName,
        OriginalFileName = originalFileName,
    };
}

/// <summary>
/// Builds file and folder names from the naming formats in Settings → Media Management, with the token syntax of
/// Radarr and Sonarr: <c>{Movie Title}</c>, <c>{Movie.Title}</c> (dots instead of spaces), <c>{MOVIE TITLE}</c> (upper case),
/// <c>{season:00}</c> (number format), <c>{[Quality Full]}</c> and <c>{-Release Group}</c> (prefix and suffix that disappear
/// with an empty value), <c>{{</c> and <c>}}</c> for literal braces. Colons and characters Windows does not allow are
/// replaced as configured, so the names work on every system. <c>{Custom Formats}</c> lists the matching custom formats
/// marked "include in rename" (space separated, like Radarr); <c>{Custom Formats:-DV,HDR}</c> leaves some out,
/// <c>{Custom Formats:DV,HDR}</c> keeps only those, and <c>{Custom Format:DV}</c> shows one format by name.
/// </summary>
public sealed partial class FileNameBuilder(MediaManagementValues settings)
{
    public const int MaxNameLength = 230;

    private static readonly string[] IsoCodes =
    [
        "en", "fr", "es", "de", "it", "da", "nl", "ja", "is", "zh", "ru", "pl", "vi", "sv", "no", "fi", "tr", "pt", "el", "ko", "hu", "he",
        "lt", "cs", "hi", "ro", "th", "bg", "ar", "uk", "fa", "bn", "sk", "lv", "ca", "hr", "sr", "bs", "et", "ta", "id", "te", "mk", "sl",
        "ml", "kn", "sq", "af", "mr", "tl", "ur", "rm", "mn", "ka",
    ];

    private static readonly Dictionary<int, string> IsoByLanguage = IsoCodes
        .Select(code => (Language.FromIsoCode(code), code))
        .Where(x => x.Item1 != Language.Unknown)
        .GroupBy(x => x.Item1.Id)
        .ToDictionary(g => g.Key, g => g.First().code.ToUpperInvariant());

    private static readonly string[] MovieTitleTokens = ["movie title", "movie cleantitle", "movie titlethe", "movie originaltitle", "movie titlefirstcharacter"];
    private static readonly string[] SeriesTitleTokens =
        ["series title", "series cleantitle", "series titleyear", "series cleantitleyear", "series titlethe", "series titlefirstcharacter"];
    private static readonly string[] FileTokens =
    [
        "quality full", "quality title", "quality proper", "quality real", "release group", "edition tags", "mediainfo videocodec", "mediainfo audiocodec", "mediainfo simple",
        "mediainfo videodynamicrange", "mediainfo audiolanguages", "original title", "original filename", "custom formats", "custom format",
    ];

    /// <summary>Tokens that take a filter after the colon (<c>{Custom Formats:-x265}</c>) instead of a number format.</summary>
    private static readonly IReadOnlySet<string> FilterTokens = new HashSet<string> { "custom formats", "custom format" };

    private static readonly IReadOnlySet<string> MovieIdTokens = new HashSet<string> { "imdb id", "imdbid", "tmdb id", "tmdbid" };
    private static readonly IReadOnlySet<string> SeriesIdTokens = new HashSet<string> { "imdb id", "imdbid", "tmdb id", "tmdbid", "tvdb id", "tvdbid" };

    private static readonly Dictionary<NamingFormatKind, IReadOnlySet<string>> Allowed = new()
    {
        [NamingFormatKind.MovieFile] = Set(MovieTitleTokens, ["release year"], FileTokens, MovieIdTokens),
        [NamingFormatKind.MovieFolder] = Set(MovieTitleTokens, ["release year"], MovieIdTokens),
        [NamingFormatKind.StandardEpisode] = Set(SeriesTitleTokens, EpisodeTokens(), FileTokens, SeriesIdTokens),
        [NamingFormatKind.DailyEpisode] = Set(SeriesTitleTokens, EpisodeTokens(), FileTokens, SeriesIdTokens),
        [NamingFormatKind.AnimeEpisode] = Set(SeriesTitleTokens, EpisodeTokens(), FileTokens, SeriesIdTokens),
        [NamingFormatKind.SeriesFolder] = Set(SeriesTitleTokens, ["series year"], SeriesIdTokens),
        [NamingFormatKind.SeasonFolder] = Set(SeriesTitleTokens, ["series year", "season"], SeriesIdTokens),
        [NamingFormatKind.SpecialsFolder] = Set(SeriesTitleTokens, ["series year", "season"], SeriesIdTokens),
    };

    private static string[] EpisodeTokens() => ["series year", "season", "episode", "episode title", "episode cleantitle", "air date", "absolute"];

    private static HashSet<string> Set(params IEnumerable<string>[] groups) => [.. groups.SelectMany(g => g)];

    // ------------------------------------------------------------------ movies

    /// <summary>File name without extension.</summary>
    public string MovieFileName(Movie movie, NamingFileInfo file)
    {
        ArgumentNullException.ThrowIfNull(movie);
        return Build(settings.StandardMovieFormat, new Context(movie, null, [], file, null));
    }

    public string MovieFolderName(Movie movie)
    {
        ArgumentNullException.ThrowIfNull(movie);
        return Build(settings.MovieFolderFormat, new Context(movie, null, [], null, null));
    }

    // ------------------------------------------------------------------ series

    /// <summary>File name without extension for one or more episodes of the same season.</summary>
    public string EpisodeFileName(Series series, IReadOnlyList<Episode> episodes, NamingFileInfo file, EpisodeFormat format = EpisodeFormat.Standard)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(episodes);
        var ordered = episodes.OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToList();

        // Like Sonarr: the daily format needs an air date, the anime format absolute numbers (specials have none).
        if ((format == EpisodeFormat.Daily && ordered.FirstOrDefault()?.AirDate is null)
            || (format == EpisodeFormat.Anime && (ordered.Count == 0 || ordered.Any(e => e.AbsoluteEpisodeNumber is null))))
        {
            format = EpisodeFormat.Standard;
        }

        var pattern = format switch
        {
            EpisodeFormat.Daily => settings.DailyEpisodeFormat,
            EpisodeFormat.Anime => settings.AnimeEpisodeFormat,
            _ => settings.StandardEpisodeFormat,
        };

        if (ordered.Count > 1)
        {
            pattern = ExpandMultiEpisode(pattern, ordered);
        }

        return Build(pattern, new Context(null, series, ordered, file, ordered.FirstOrDefault()?.SeasonNumber));
    }

    public string SeriesFolderName(Series series)
    {
        ArgumentNullException.ThrowIfNull(series);
        return Build(settings.SeriesFolderFormat, new Context(null, series, [], null, null));
    }

    /// <summary>The season folder ("Season 1", "Specials"), or null without season folders.</summary>
    public string? SeasonFolderName(Series series, int seasonNumber)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (!settings.UseSeasonFolders)
        {
            return null;
        }

        var format = seasonNumber == 0 ? settings.SpecialsFolderFormat : settings.SeasonFolderFormat;
        var name = Build(format, new Context(null, series, [], null, seasonNumber));
        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// The format of the series type (Sonarr): Daily and Anime series use their formats; a Standard series uses the daily
    /// format only for a file or release named by air date.
    /// </summary>
    public static EpisodeFormat FormatFor(Series series, bool namedByAirDate)
    {
        ArgumentNullException.ThrowIfNull(series);
        return series.SeriesType switch
        {
            SeriesType.Daily => EpisodeFormat.Daily,
            SeriesType.Anime => EpisodeFormat.Anime,
            _ => namedByAirDate ? EpisodeFormat.Daily : EpisodeFormat.Standard,
        };
    }

    // ------------------------------------------------------------------ validation

    /// <summary>Null when the format is usable, otherwise what is wrong with it.</summary>
    public static string? ValidateFormat(string? format, NamingFormatKind kind)
    {
        var text = format?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return kind == NamingFormatKind.SpecialsFolder ? "Enter a folder name, e.g. Specials." : "Enter a format.";
        }

        if (text.Length > 300)
        {
            return "Use at most 300 characters.";
        }

        var used = new List<string>();
        var rest = LabelRegex().Replace(text, m => m.Groups["inner"].Value);
        var badFilter = false;
        rest = TokenRegex().Replace(rest, m =>
        {
            if (!m.Groups["escaped"].Success)
            {
                var key = Key(m.Groups["token"].Value);
                used.Add(key);
                badFilter |= m.Groups["filter"].Success && !FilterTokens.Contains(key);
            }

            return string.Empty;
        });

        if (badFilter)
        {
            return "Only number formats like {season:00} may follow a colon here; {Custom Formats:…} takes format names.";
        }

        if (rest.Contains('{', StringComparison.Ordinal) || rest.Contains('}', StringComparison.Ordinal))
        {
            return "A brace is not closed or a token is written wrong. Use {{ and }} for literal braces.";
        }

        if (rest.IndexOfAny(['/', '\\']) >= 0)
        {
            return "Subfolders are not supported here; remove / and \\.";
        }

        if (rest.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0 || rest.Any(char.IsControl))
        {
            return "Remove the characters < > : \" | ? * (not allowed in file names on Windows).";
        }

        var allowed = Allowed[kind];
        if (used.FirstOrDefault(t => !allowed.Contains(t)) is { } unknown)
        {
            return $"Unknown token {{{Display(unknown)}}} here. See the token list.";
        }

        bool Has(params string[] tokens) => used.Any(tokens.Contains);
        var original = Has("original title", "original filename");
        return kind switch
        {
            NamingFormatKind.MovieFile when !Has(MovieTitleTokens) && !original => "Add the movie title, e.g. {Movie Title}.",
            NamingFormatKind.MovieFolder when !Has(MovieTitleTokens) => "Add the movie title, e.g. {Movie Title}.",
            NamingFormatKind.StandardEpisode when !(Has("season") && Has("episode")) && !original =>
                "Add the season and episode numbers, e.g. S{season:00}E{episode:00}.",
            NamingFormatKind.AnimeEpisode when !(Has("season") && Has("episode")) && !Has("absolute") && !original =>
                "Add the season and episode numbers or the absolute number, e.g. S{season:00}E{episode:00} or {absolute:000}.",
            NamingFormatKind.DailyEpisode when !Has("air date") && !(Has("season") && Has("episode")) && !original =>
                "Add the air date, e.g. {Air-Date}.",
            NamingFormatKind.SeriesFolder when !Has(SeriesTitleTokens) => "Add the series title, e.g. {Series Title}.",
            NamingFormatKind.SeasonFolder when !Has("season") => "Add the season number, e.g. Season {season}.",
            _ => null,
        };
    }

    // ------------------------------------------------------------------ building

    private sealed record Context(Movie? Movie, Series? Series, IReadOnlyList<Episode> Episodes, NamingFileInfo? File, int? Season);

    private string Build(string format, Context context)
    {
        // Plex style "{edition-{Edition Tags}}": kept with its braces, or gone with an empty value.
        format = LabelRegex().Replace(format, m =>
        {
            var inner = Build(m.Groups["inner"].Value, context);
            return inner.Length == 0 ? string.Empty : $"{{{{{m.Groups["label"].Value}-{inner.Replace("{", string.Empty, StringComparison.Ordinal).Replace("}", string.Empty, StringComparison.Ordinal)}}}}}";
        });

        var built = TokenRegex().Replace(format, m =>
        {
            if (m.Groups["escaped"].Success)
            {
                return m.Value[..1];
            }

            var token = m.Groups["token"].Value;
            var key = Key(token);
            var value = FilterTokens.Contains(key)
                ? CustomFormatValue(key, m.Groups["filter"].Value, context.File)
                : m.Groups["filter"].Success ? null : Value(key, m.Groups["format"].Value, context);
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            value = CleanValue(value);
            var separator = m.Groups["separator"].Value;
            if (separator.Length > 0 && separator != " ")
            {
                value = value.Replace(" ", separator, StringComparison.Ordinal);
            }

            if (token.All(c => !char.IsLetter(c) || char.IsLower(c)))
            {
                value = value.ToLowerInvariant();
            }
            else if (token.All(c => !char.IsLetter(c) || char.IsUpper(c)))
            {
                value = value.ToUpperInvariant();
            }

            return m.Groups["prefix"].Value + value + m.Groups["suffix"].Value;
        });

        return Tidy(built);
    }

    private static string? Value(string key, string numberFormat, Context c)
    {
        var movie = c.Movie;
        var series = c.Series;
        var file = c.File;
        return key switch
        {
            "movie title" => movie?.Title,
            "movie cleantitle" => movie is null ? null : CleanTitle(movie.Title),
            "movie titlethe" => movie is null ? null : TitleThe(movie.Title),
            "movie originaltitle" => movie is null ? null : (string.IsNullOrWhiteSpace(movie.OriginalTitle) ? movie.Title : movie.OriginalTitle),
            "movie titlefirstcharacter" => movie is null ? null : FirstCharacter(movie.Title),
            "release year" => movie is { Year: > 0 } ? movie.Year.ToString(CultureInfo.InvariantCulture) : null,
            "series title" => series?.Title,
            "series cleantitle" => series is null ? null : CleanTitle(series.Title),
            "series titleyear" => series is null ? null : WithYear(series.Title, series.Year),
            "series cleantitleyear" => series is null ? null : WithYear(CleanTitle(series.Title), series.Year),
            "series titlethe" => series is null ? null : TitleThe(series.Title),
            "series titlefirstcharacter" => series is null ? null : FirstCharacter(series.Title),
            "series year" => series is { Year: > 0 } ? series.Year.ToString(CultureInfo.InvariantCulture) : null,
            "season" => c.Season is { } season ? Number(season, numberFormat) : null,
            "episode" => c.Episodes.Count == 0 ? null : string.Join('-', c.Episodes.Select(e => Number(e.EpisodeNumber, numberFormat))),
            "episode title" => EpisodeTitle(c.Episodes),
            "episode cleantitle" => EpisodeTitle(c.Episodes) is { } title ? CleanTitle(title) : null,
            "air date" => c.Episodes.FirstOrDefault()?.AirDate?.Replace('-', ' '),
            "absolute" => AbsoluteValue(c.Episodes, numberFormat),
            "quality full" => file is null || file.Quality.Quality == Core.Quality.Quality.Unknown ? null : QualityFull(file.Quality),
            "quality title" => file is null || file.Quality.Quality == Core.Quality.Quality.Unknown ? null : file.Quality.Quality.Name,
            "quality proper" => file is { Quality.Revision.Version: > 1 } ? (file.Quality.Revision.IsRepack ? "Repack" : "Proper") : null,
            "quality real" => file is { Quality.Revision.Real: > 0 } ? "REAL" : null,
            "release group" => file?.ReleaseGroup,
            "edition tags" => file?.Edition,
            "mediainfo videocodec" => file?.VideoCodec,
            "mediainfo audiocodec" => file?.AudioCodec,
            "mediainfo simple" => file is null ? null : string.Join(' ', new[] { file.VideoCodec, file.AudioCodec }.Where(v => !string.IsNullOrEmpty(v))),
            "mediainfo videodynamicrange" => file is { Hdr: not HdrFormat.None } ? "HDR" : null,
            "mediainfo audiolanguages" => file is null ? null : AudioLanguages(file.Languages),
            "original title" => file?.SceneName,
            "original filename" => file?.OriginalFileName,
            "imdb id" or "imdbid" => movie?.ImdbId ?? series?.ImdbId,
            "tmdb id" or "tmdbid" => (movie?.TmdbId ?? series?.TmdbId) is > 0 and var tmdb ? tmdb.ToString(CultureInfo.InvariantCulture) : null,
            "tvdb id" or "tvdbid" => series?.TvdbId is > 0 and var tvdb ? tvdb.ToString(CultureInfo.InvariantCulture) : null,
            _ => null,
        };
    }

    /// <summary>{absolute:000}: "012", several episodes as a range "012-014"; nothing when an episode has no absolute number.</summary>
    private static string? AbsoluteValue(IReadOnlyList<Episode> episodes, string numberFormat)
    {
        if (episodes.Count == 0 || episodes.Any(e => e.AbsoluteEpisodeNumber is null))
        {
            return null;
        }

        var numbers = episodes.Select(e => e.AbsoluteEpisodeNumber!.Value).Distinct().Order().ToList();
        return numbers.Count == 1
            ? Number(numbers[0], numberFormat)
            : $"{Number(numbers[0], numberFormat)}-{Number(numbers[^1], numberFormat)}";
    }

    /// <summary>
    /// Radarr's custom format tokens: the matching formats marked "include in rename", joined with spaces; a filter
    /// "-A,B" leaves A and B out, "A,B" keeps only them; {Custom Format:A} is format A when it matches.
    /// </summary>
    private static string? CustomFormatValue(string key, string filter, NamingFileInfo? file)
    {
        if (file is null)
        {
            return null;
        }

        var formats = file.CustomFormats.Where(f => f.IncludeInRename).ToList();
        var names = filter.TrimStart('-').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Named(CustomFormat f) => names.Contains(f.Name, StringComparer.OrdinalIgnoreCase);

        if (key == "custom format")
        {
            return filter.Length == 0 ? null : formats.FirstOrDefault(Named)?.Name;
        }

        if (filter.Length > 0)
        {
            formats = filter.StartsWith('-') ? [.. formats.Where(f => !Named(f))] : [.. formats.Where(Named)];
        }

        return formats.Count == 0 ? null : string.Join(' ', formats.Select(f => f.Name));
    }

    /// <summary>
    /// Replaces the season-episode part of the format (e.g. <c>S{season:00}E{episode:00}</c>) with the text for several
    /// episodes, in the configured multi-episode style.
    /// </summary>
    private string ExpandMultiEpisode(string format, IReadOnlyList<Episode> episodes)
    {
        var match = SeasonEpisodeRegex().Match(format);
        if (!match.Success)
        {
            return format;
        }

        var season = Number(episodes[0].SeasonNumber, match.Groups["seasonFormat"].Value);
        var numbers = episodes.Select(e => Number(e.EpisodeNumber, match.Groups["episodeFormat"].Value)).Distinct().ToList();
        var prefix = match.Groups["prefix"].Value;
        var sep = match.Groups["sep"].Value;
        var lead = prefix + season + sep + numbers[0];
        var text = settings.MultiEpisodeStyle switch
        {
            MultiEpisodeStyle.Extend => lead + string.Concat(numbers.Skip(1).Select(n => "-" + n)),
            MultiEpisodeStyle.Duplicate => string.Join(DuplicateSeparator(format, match.Index), numbers.Select(n => prefix + season + sep + n)),
            MultiEpisodeStyle.Repeat => lead + string.Concat(numbers.Skip(1).Select(n => sep + n)),
            MultiEpisodeStyle.Scene => lead + string.Concat(numbers.Skip(1).Select(n => "-" + sep + n)),
            MultiEpisodeStyle.Range => lead + "-" + numbers[^1],
            _ => lead + "-" + sep + numbers[^1],
        };

        // Braces in the generated text cannot happen (numbers, the season prefix and the separator only).
        return format[..match.Index] + text + format[(match.Index + match.Length)..];
    }

    private static string DuplicateSeparator(string format, int index)
    {
        var before = format[..index];
        if (before.EndsWith(" - ", StringComparison.Ordinal))
        {
            return " - ";
        }

        return before.Length > 0 && before[^1] is '.' or '_' or '-' or ' ' ? before[^1].ToString() : " ";
    }

    // ------------------------------------------------------------------ values

    private static string Number(int value, string format) =>
        format.Length > 0 ? value.ToString(format, CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);

    private static string WithYear(string title, int year) =>
        year > 0 && !title.EndsWith($"({year})", StringComparison.Ordinal) ? $"{title} ({year})" : title;

    private static string? EpisodeTitle(IReadOnlyList<Episode> episodes)
    {
        var titles = episodes.Select(e => e.Title?.Trim()).Where(t => !string.IsNullOrEmpty(t)).Select(t => t!).ToList();
        if (titles.Count == 0)
        {
            return null;
        }

        if (titles.Count == 1)
        {
            return titles[0];
        }

        // "Pilot (1)", "Pilot (2)" → "Pilot"; otherwise all titles.
        var bases = titles.Select(t => PartNumberRegex().Replace(t, string.Empty)).Distinct(StringComparer.Ordinal).ToList();
        return bases.Count == 1 ? bases[0] : string.Join(" + ", titles.Distinct(StringComparer.Ordinal));
    }

    private static string QualityFull(QualityModel quality)
    {
        var text = quality.Quality.Name;
        if (quality.Revision.Real > 0)
        {
            text += " REAL";
        }

        if (quality.Revision.Version > 1)
        {
            text += quality.Revision.IsRepack ? " Repack" : " Proper";
        }

        return text;
    }

    private static string? AudioLanguages(IReadOnlyList<Language> languages)
    {
        var codes = languages.Where(l => l.Id > 0).Select(l => IsoByLanguage.GetValueOrDefault(l.Id)).Where(c => c is not null).Distinct().ToList();
        return codes.Count == 0 ? null : string.Join('+', codes);
    }

    /// <summary>"The Matrix" → "Matrix, The" (Radarr's TitleThe).</summary>
    public static string TitleThe(string title)
    {
        var match = ArticleRegex().Match(title.Trim());
        return match.Success ? $"{match.Groups["rest"].Value}, {match.Groups["article"].Value}" : title;
    }

    /// <summary>Title without punctuation: "Dune: Part Two" → "Dune Part Two", "Fast & Furious" → "Fast and Furious".</summary>
    public static string CleanTitle(string title)
    {
        var text = title.Replace("&", "and", StringComparison.Ordinal);
        text = SlashRegex().Replace(text, " ");
        text = LonePunctuationRegex().Replace(text, " ");
        text = PunctuationRegex().Replace(text, string.Empty);
        return SpacesRegex().Replace(text, " ").Trim();
    }

    private static string FirstCharacter(string title)
    {
        var first = TitleThe(title).TrimStart().FirstOrDefault();
        return char.IsLetterOrDigit(first) ? char.ToUpperInvariant(first).ToString() : "_";
    }

    // ------------------------------------------------------------------ cleaning

    /// <summary>Colons as configured, then characters Windows does not allow in names (Sonarr's CleanFileName).</summary>
    private string CleanValue(string value)
    {
        var text = settings.ColonReplacement switch
        {
            ColonReplacement.Delete => value.Replace(":", string.Empty, StringComparison.Ordinal),
            ColonReplacement.Dash => value.Replace(":", "-", StringComparison.Ordinal),
            ColonReplacement.SpaceDash => value.Replace(":", " -", StringComparison.Ordinal),
            ColonReplacement.SpaceDashSpace => value.Replace(":", " - ", StringComparison.Ordinal),
            _ => SmartColonRegex().Replace(value, m => m.Value.Length > 1 ? " - " : "-"),
        };

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsControl(c))
            {
                continue;
            }

            var replacement = c switch
            {
                '\\' or '/' => "+",
                '<' or '>' or '|' or '"' => string.Empty,
                '?' => "!",
                '*' => "-",
                ':' => "-",
                _ => null,
            };

            if (replacement is null)
            {
                builder.Append(c);
            }
            else if (settings.ReplaceIllegalCharacters)
            {
                builder.Append(replacement);
            }
        }

        return builder.ToString();
    }

    /// <summary>Removes what empty tokens leave behind (empty brackets, double separators) and makes the name safe everywhere.</summary>
    private static string Tidy(string name)
    {
        var text = EmptyBracketsRegex().Replace(name, string.Empty);
        text = SpacesRegex().Replace(text, " ");
        text = DashRunRegex().Replace(text, " - ");
        text = RepeatedSeparatorRegex().Replace(text, "$1");
        text = new string([.. text.Where(c => !char.IsControl(c) && c is not ('/' or '\\' or '<' or '>' or ':' or '"' or '|' or '?' or '*'))]);
        text = text.Trim(' ', '.', '-', '_');
        if (text.Length > MaxNameLength)
        {
            text = text[..MaxNameLength].TrimEnd(' ', '.', '-', '_');
        }

        if (ReservedNameRegex().IsMatch(text))
        {
            text += "_";
        }

        return text;
    }

    // ------------------------------------------------------------------ tokens

    /// <summary>"Movie.Title" → "movie title", "Air-Date" → "air date".</summary>
    private static string Key(string token)
    {
        var match = TokenWordsRegex().Match(token);
        return match.Success && match.Groups["second"].Success
            ? $"{match.Groups["first"].Value} {match.Groups["second"].Value}".ToLowerInvariant()
            : token.ToLowerInvariant();
    }

    private static string Display(string key) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key);

    [GeneratedRegex(@"(?<escaped>\{\{|\}\})|\{(?<prefix>[- ._\[(]*)(?<token>[a-z0-9]+(?:(?<separator>[- ._]+)[a-z0-9]+)?)(?::(?:(?<format>0+)|(?<filter>[ ,a-z0-9+-]+(?<![- ]))))?(?<suffix>[- ._)\]]*)\}", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"\{(?<label>[a-z]+)-(?<inner>\{[^{}]+\})\}", RegexOptions.IgnoreCase)]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"^(?<first>[a-z0-9]+)[- ._]+(?<second>[a-z0-9]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex TokenWordsRegex();

    [GeneratedRegex(@"(?<prefix>S?)\{season(?::(?<seasonFormat>0+))?\}(?<sep>[^{}]{0,3}?)\{episode(?::(?<episodeFormat>0+))?\}", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"\s?\(\d+\)$")]
    private static partial Regex PartNumberRegex();

    [GeneratedRegex(@"^(?<article>The|An|A)\s+(?<rest>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ArticleRegex();

    [GeneratedRegex(@"[/\\]")]
    private static partial Regex SlashRegex();

    [GeneratedRegex(@"(?<=\s)[,<>;:'""|`~!?@$%^*\-_=]+(?=\s)")]
    private static partial Regex LonePunctuationRegex();

    [GeneratedRegex(@"[,<>;:'""|`~!?@$%^*=.()\[\]{}]")]
    private static partial Regex PunctuationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();

    [GeneratedRegex(@":\s?")]
    private static partial Regex SmartColonRegex();

    [GeneratedRegex(@"\(\s*\)|\[\s*\]|\{\s*\}")]
    private static partial Regex EmptyBracketsRegex();

    [GeneratedRegex(@"\s-(?:\s+-)+\s")]
    private static partial Regex DashRunRegex();

    [GeneratedRegex(@"([._])\1+")]
    private static partial Regex RepeatedSeparatorRegex();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedNameRegex();
}
