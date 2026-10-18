// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Library.CustomFormats;
using StreamZH.Library.Import;
using StreamZH.Metadata.Tmdb;
using StreamZH.Parsing;
using StreamZH.Parsing.Releases;

namespace StreamZH.Library.Renamer;

/// <summary>What the media rule found for a file: the movie, or the series and its episodes (for the folder format too).</summary>
public sealed record MediaContext(Movie? Movie, Series? Series, IReadOnlyList<Episode> Episodes, int? Season, MediaMatchInfo Info);

/// <param name="Name">The new name with extension, or null with an <paramref name="Error"/>.</param>
public sealed record MediaResult(string? Name, MediaContext? Context, string? Error);

/// <summary>The library item the source folder belongs to: every file is matched to it instead of a search.</summary>
public sealed record MediaHint(int? MovieId, int? SeriesId);

/// <summary>
/// The media rule: reads a name with StreamZH's release parser (SxxEyy, 1x02, daily dates, absolute numbers, movie title
/// and year), finds the movie or the episodes in the library or on TMDB and builds the name with <see cref="FileNameBuilder"/>
/// and the naming formats (from the rule, or from Settings → Media Management). Episode titles come from the library or
/// TMDB, so even garbage names get the right titles as long as the season and episode can be read.
/// </summary>
public sealed partial class MediaNamer(MediaManagementValues settings, RenamerCatalog catalog, MediaHint hint)
{
    private readonly Dictionary<string, (Series? Series, string Source, string? Note)> _seriesMatches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Movie? Movie, string Source, string? Note)> _movieMatches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<Episode>> _tmdbEpisodes = new(StringComparer.Ordinal);

    /// <summary>Null when the rule can be used.</summary>
    public static string? Check(MediaRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.EpisodeFormat.Trim().Length > 0
            && FileNameBuilder.ValidateFormat(rule.EpisodeFormat, NamingFormatKind.StandardEpisode) is { } episodeError
            && FileNameBuilder.ValidateFormat(rule.EpisodeFormat, NamingFormatKind.DailyEpisode) is not null)
        {
            return "Episode format: " + episodeError;
        }

        if (rule.MovieFormat.Trim().Length > 0 && FileNameBuilder.ValidateFormat(rule.MovieFormat, NamingFormatKind.MovieFile) is { } movieError)
        {
            return "Movie format: " + movieError;
        }

        if (rule.Season is < 0 or > 1000)
        {
            return "The season is 0 to 1000.";
        }

        if (rule.EpisodeOffset is < -1000 or > 1000)
        {
            return "The episode offset is -1000 to 1000.";
        }

        if (rule.TmdbId is <= 0)
        {
            return "Enter a TMDB id above 0, or leave it empty.";
        }

        return rule.SearchTitle.Length > 200 ? "Use at most 200 characters for the title." : null;
    }

    /// <summary>Names one file. <paramref name="currentName"/> is the name the rules before built (identification),
    /// <paramref name="item"/> the original (quality and release group).</summary>
    public async Task<MediaResult> NameAsync(MediaRule rule, string currentName, string fullPath, RenameItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(currentName);
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsFolder)
        {
            return new MediaResult(currentName, null, null);
        }

        var (stem, extension) = RuleEngine.Split(currentName, false);
        var folder = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var kind = KindOf(rule, stem, folder);
        try
        {
            var result = kind == MediaKind.Movie
                ? await MovieAsync(rule, stem, folder, fullPath, item, cancellationToken)
                : await EpisodeAsync(rule, stem, folder, fullPath, item, cancellationToken);
            if (result.Name is { } built)
            {
                // Subtitles keep their language: "Show.S01E01.de.forced.srt" → "<name>.de.forced.srt".
                var suffix = DiskScanner.VideoExtensions.Contains(extension) ? string.Empty : SubtitleSuffixRegex().Match(stem).Value;
                return result with { Name = built + suffix + extension };
            }

            return result;
        }
        catch (RuleException ex)
        {
            return new MediaResult(null, null, ex.Message);
        }
    }

    private MediaKind KindOf(MediaRule rule, string stem, string folder)
    {
        if (rule.Kind != MediaKind.Auto)
        {
            return rule.Kind;
        }

        if (hint.SeriesId is not null || rule.Season is not null)
        {
            return MediaKind.Episode;
        }

        if (hint.MovieId is not null)
        {
            return MediaKind.Movie;
        }

        if (ReleaseParser.Parse(stem) is { IsEpisode: true } || EpisodeFileParser.Parse(stem) is not null)
        {
            return MediaKind.Episode;
        }

        return SeasonOfFolder(Path.GetFileName(folder)) is not null ? MediaKind.Episode : MediaKind.Movie;
    }

    // ------------------------------------------------------------------ episodes

    private async Task<MediaResult> EpisodeAsync(MediaRule rule, string stem, string folder, string fullPath, RenameItem item, CancellationToken ct)
    {
        var parsed = ReleaseParser.ParseEpisode(stem);
        int? season = parsed?.SeasonNumber;
        var numbers = parsed?.EpisodeNumbers ?? [];
        var absolute = parsed?.AbsoluteEpisodeNumbers ?? [];
        var airDate = parsed?.AirDate;
        if (numbers.Count == 0 && airDate is null && EpisodeFileParser.Parse(stem) is { } simple)
        {
            season = simple.Season;
            numbers = simple.Episodes;
        }

        var folderName = Path.GetFileName(folder);
        var folderSeason = SeasonOfFolder(folderName);
        var showFolder = folderSeason is not null ? Path.GetFileName(Path.GetDirectoryName(folder) ?? string.Empty) : folderName;

        if (numbers.Count == 0 && airDate is null && absolute.Count == 0)
        {
            if (LooseEpisode(stem) is { } loose && (rule.Season ?? folderSeason) is { } chosen)
            {
                season = chosen;
                numbers = [loose];
            }
            else
            {
                throw new RuleException(LooseEpisode(stem) is null
                    ? "No season and episode found in the name (like S01E02 or 1x02)."
                    : "Only an episode number in the name. Set the season in the media rule.");
            }
        }

        if (season is null && numbers.Count > 0 && (rule.Season ?? folderSeason) is { } fallback)
        {
            season = fallback;
        }

        if (rule.Season is { } overrideSeason && airDate is null)
        {
            season = overrideSeason;
            if (numbers.Count == 0 && absolute.Count > 0)
            {
                numbers = absolute;
                absolute = [];
            }
        }

        // Title to look up: the rule's, the name's, or the folder's ("Breaking Bad/Season 1/01.mkv").
        var title = rule.SearchTitle.Trim();
        var year = 0;
        if (title.Length == 0 && parsed is { Title.Length: > 0 } && !parsed.Title.All(c => char.IsDigit(c) || c == ' '))
        {
            title = parsed.Title;
            year = parsed.Year;
        }

        if (title.Length == 0 && rule.UseFolderName && showFolder.Length > 0)
        {
            var fromFolder = FolderNameParser.Parse(showFolder);
            title = fromFolder.Title;
            year = fromFolder.Year ?? 0;
        }

        title = TrailingEpisodeWordRegex().Replace(title, string.Empty).Trim();
        var (series, source, note) = await SeriesAsync(rule, title, year, ct);
        if (series is null)
        {
            if (title.Length == 0)
            {
                throw new RuleException("No series title in the name. Turn on \"Title from the folder\" or enter the title in the rule.");
            }

            series = new Series { Title = title, Year = year };
        }

        var known = await KnownEpisodesAsync(series, source, season, airDate is not null || (absolute.Count > 0 && numbers.Count == 0), ct);
        List<Episode> episodes;
        var daily = false;
        if (airDate is not null && numbers.Count == 0)
        {
            daily = true;
            var onDate = known.Where(e => e.AirDate == airDate).OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToList();
            episodes = onDate.Count > 0 ? [onDate[0]] : [new Episode { SeasonNumber = int.Parse(airDate[..4], CultureInfo.InvariantCulture), AirDate = airDate }];
            if (onDate.Count == 0 && source != "name")
            {
                note = Join(note, $"no episode aired on {airDate}");
            }
        }
        else if (numbers.Count == 0 && absolute.Count > 0)
        {
            var ordered = known.Where(e => e.SeasonNumber > 0).OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToList();
            if (ordered.Count == 0)
            {
                throw new RuleException("Only an absolute episode number. Set the season in the media rule, or match the series on TMDB.");
            }

            episodes = [];
            foreach (var number in absolute.Select(n => n + rule.EpisodeOffset))
            {
                // Library series: TheXEM's scene absolute number first (release names), then the counted absolute number.
                var scene = known.Where(e => e.SceneAbsoluteEpisodeNumber == number).ToList();
                var found = scene.Count == 1 ? scene[0]
                    : known.FirstOrDefault(e => e.AbsoluteEpisodeNumber == number)
                    ?? (number >= 1 && number <= ordered.Count ? ordered[number - 1] : null);
                if (found is null)
                {
                    throw new RuleException($"Absolute episode {number.ToString(CultureInfo.InvariantCulture)} is not known.");
                }

                episodes.Add(found);
            }

            season = episodes[0].SeasonNumber;
        }
        else
        {
            var s = season ?? 1;
            season = s;
            episodes = [];
            var missing = new List<int>();
            foreach (var number in numbers.Select(n => n + rule.EpisodeOffset).Distinct())
            {
                if (number < 0)
                {
                    throw new RuleException("The episode offset makes an episode number below 0.");
                }

                var found = known.FirstOrDefault(e => e.SeasonNumber == s && e.EpisodeNumber == number);
                if (found is null)
                {
                    missing.Add(number);
                }

                episodes.Add(found ?? new Episode { SeasonNumber = s, EpisodeNumber = number });
            }

            if (missing.Count > 0 && source != "name")
            {
                note = Join(note, $"{string.Join(", ", missing.Select(m => $"S{s:00}E{m:00}"))} not found");
            }
        }

        var values = Values(rule);
        var builder = new FileNameBuilder(values);
        var info = FileInfoFor(fullPath, item).WithCustomFormats(
            catalog.Formats(MediaType.Series), MediaType.Series, Language.FindById(series.OriginalLanguageId), series.Year, Path.GetFileName(fullPath),
            CustomFormatNaming.SizeOf(fullPath), CustomFormatNaming.EpisodeFileType(episodes.Count));
        var name = builder.EpisodeFileName(series, episodes, info, FileNameBuilder.FormatFor(series, daily));
        if (name.Length == 0)
        {
            throw new RuleException("The episode format gave an empty name.");
        }

        var sorted = episodes.OrderBy(e => e.EpisodeNumber).ToList();
        var episodeTitle = string.Join(" + ", sorted.Select(e => e.Title).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal));
        var match = new MediaMatchInfo(
            "episode",
            series.Title,
            series.Year,
            daily ? null : season,
            daily ? [] : [.. sorted.Select(e => e.EpisodeNumber)],
            episodeTitle.Length == 0 ? null : episodeTitle,
            source,
            note);
        return new MediaResult(name, new MediaContext(null, series, sorted, sorted.FirstOrDefault()?.SeasonNumber ?? season, match), null);
    }

    private async Task<(Series? Series, string Source, string? Note)> SeriesAsync(MediaRule rule, string title, int year, CancellationToken ct)
    {
        if (hint.SeriesId is { } forced && catalog.FindSeries(forced) is { } item)
        {
            return (item, "library", null);
        }

        var key = $"{rule.Match}|{rule.TmdbId}|{TitleNormalizer.Clean(title)}|{year}";
        if (_seriesMatches.TryGetValue(key, out var cached))
        {
            return cached;
        }

        (Series?, string, string?) result = (null, "name", null);
        if (rule.Match is MediaMatch.LibraryAndTmdb or MediaMatch.Library)
        {
            var library = rule.TmdbId is { } id ? catalog.Series.FirstOrDefault(s => s.TmdbId == id) : catalog.MatchSeries(title, year);
            if (library is not null)
            {
                result = (library, "library", null);
            }
        }

        if (result.Item1 is null && rule.Match is MediaMatch.LibraryAndTmdb or MediaMatch.Tmdb)
        {
            var found = await TmdbAsync(() => rule.TmdbId is { } id ? catalog.TmdbSeriesAsync(id, ct) : catalog.SearchTmdbSeriesAsync(title, year, ct));
            result = found is null ? (null, "name", catalog.TmdbConfigured ? "not found on TMDB" : null) : (found, "tmdb", null);
        }

        _seriesMatches[key] = result;
        return result;
    }

    private async Task<IReadOnlyList<Episode>> KnownEpisodesAsync(Series series, string source, int? season, bool allSeasons, CancellationToken ct)
    {
        if (source == "library")
        {
            return catalog.Episodes(series.Id);
        }

        if (source != "tmdb" || series.TmdbId <= 0)
        {
            return [];
        }

        var key = allSeasons || season is null ? $"{series.TmdbId}:all" : $"{series.TmdbId}:{season}";
        if (_tmdbEpisodes.TryGetValue(key, out var list))
        {
            return list;
        }

        var episodes = await TmdbAsync<IReadOnlyList<Episode>>(async () =>
            allSeasons || season is null ? await catalog.TmdbAllEpisodesAsync(series.TmdbId, ct) : await catalog.TmdbSeasonAsync(series.TmdbId, season.Value, ct)) ?? [];
        _tmdbEpisodes[key] = episodes;
        return episodes;
    }

    // ------------------------------------------------------------------ movies

    private async Task<MediaResult> MovieAsync(MediaRule rule, string stem, string folder, string fullPath, RenameItem item, CancellationToken ct)
    {
        var parsed = ReleaseParser.ParseMovie(stem) ?? ReleaseParser.Parse(stem);
        var title = rule.SearchTitle.Trim();
        var year = parsed?.Year ?? 0;
        int? folderTmdbId = null;
        if (title.Length == 0 && parsed is { Title.Length: > 0 })
        {
            title = parsed.Title;
        }

        if (rule.UseFolderName && Path.GetFileName(folder) is { Length: > 0 } folderName)
        {
            var fromFolder = FolderNameParser.Parse(folderName);
            folderTmdbId = fromFolder.TmdbId;
            if (title.Length == 0)
            {
                title = fromFolder.Title;
                year = fromFolder.Year ?? year;
            }
        }

        var (movie, source, note) = await MovieMatchAsync(rule, title, year, rule.TmdbId ?? folderTmdbId, ct);
        if (movie is null)
        {
            if (title.Length == 0)
            {
                throw new RuleException("No movie title in the name.");
            }

            movie = new Movie { Title = title, Year = year };
        }

        var info = FileInfoFor(fullPath, item).WithCustomFormats(
            catalog.Formats(MediaType.Movie), MediaType.Movie, Language.FindById(movie.OriginalLanguageId), movie.Year, Path.GetFileName(fullPath),
            CustomFormatNaming.SizeOf(fullPath));
        var name = new FileNameBuilder(Values(rule)).MovieFileName(movie, info);
        if (name.Length == 0)
        {
            throw new RuleException("The movie format gave an empty name.");
        }

        var match = new MediaMatchInfo("movie", movie.Title, movie.Year, null, [], null, source, note);
        return new MediaResult(name, new MediaContext(movie, null, [], null, match), null);
    }

    private async Task<(Movie? Movie, string Source, string? Note)> MovieMatchAsync(MediaRule rule, string title, int year, int? tmdbId, CancellationToken ct)
    {
        if (hint.MovieId is { } forced && catalog.FindMovie(forced) is { } item)
        {
            return (item, "library", null);
        }

        var key = $"{rule.Match}|{tmdbId}|{TitleNormalizer.Clean(title)}|{year}";
        if (_movieMatches.TryGetValue(key, out var cached))
        {
            return cached;
        }

        (Movie?, string, string?) result = (null, "name", null);
        if (rule.Match is MediaMatch.LibraryAndTmdb or MediaMatch.Library)
        {
            var library = tmdbId is { } id ? catalog.Movies.FirstOrDefault(m => m.TmdbId == id) : catalog.MatchMovie(title, year);
            if (library is not null)
            {
                result = (library, "library", null);
            }
        }

        if (result.Item1 is null && rule.Match is MediaMatch.LibraryAndTmdb or MediaMatch.Tmdb)
        {
            var found = await TmdbAsync(() => tmdbId is { } id ? catalog.TmdbMovieAsync(id, ct) : catalog.SearchTmdbMovieAsync(title, year, ct));
            result = found is null ? (null, "name", catalog.TmdbConfigured ? "not found on TMDB" : null) : (found, "tmdb", null);
        }

        _movieMatches[key] = result;
        return result;
    }

    // ------------------------------------------------------------------ shared

    /// <summary>Null when TMDB is not set up or does not know the id; other TMDB errors become a row error.</summary>
    private async Task<T?> TmdbAsync<T>(Func<Task<T?>> call)
        where T : class
    {
        if (!catalog.TmdbConfigured)
        {
            return null;
        }

        try
        {
            return await call();
        }
        catch (TmdbException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
        catch (Exception ex) when (ex is TmdbException or HttpRequestException or TaskCanceledException)
        {
            throw new RuleException($"TMDB is not reachable: {ex.Message}");
        }
    }

    private MediaManagementValues Values(MediaRule rule)
    {
        var episode = rule.EpisodeFormat.Trim();
        var movie = rule.MovieFormat.Trim();
        return settings with
        {
            StandardEpisodeFormat = episode.Length > 0 ? episode : settings.StandardEpisodeFormat,
            DailyEpisodeFormat = episode.Length > 0 ? episode : settings.DailyEpisodeFormat,
            AnimeEpisodeFormat = episode.Length > 0 ? episode : settings.AnimeEpisodeFormat,
            StandardMovieFormat = movie.Length > 0 ? movie : settings.StandardMovieFormat,
        };
    }

    /// <summary>Quality and release group: what the import stored for the file, otherwise its original name.</summary>
    private NamingFileInfo FileInfoFor(string fullPath, RenameItem item)
    {
        var original = RuleEngine.Split(item.Name, false).Stem;
        if (catalog.Details(fullPath) is { } stored)
        {
            return NamingFileInfo.From(stored, original);
        }

        // Without the extension: a quality guessed from ".mkv" alone would invent "WEBDL-720p" for names without one.
        var parsed = ReleaseParser.Parse(original);
        return NamingFileInfo.From(parsed, null, original);
    }

    /// <summary>"Season 2", "Staffel 02", "S2" → 2; "Specials" → 0; anything else null.</summary>
    public static int? SeasonOfFolder(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (SpecialsRegex().IsMatch(name.Trim()))
        {
            return 0;
        }

        var match = SeasonFolderRegex().Match(name.Trim());
        return match.Success ? int.Parse(match.Groups["season"].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>An episode number in a name without season: "Episode 5", "E05", "05 - Title", "Title - 05".</summary>
    public static int? LooseEpisode(string stem)
    {
        ArgumentNullException.ThrowIfNull(stem);
        foreach (var regex in new[] { EpisodeWordRegex(), LeadingNumberRegex(), DashNumberRegex(), AnyNumberRegex() })
        {
            foreach (Match match in regex.Matches(stem))
            {
                var value = int.Parse(match.Groups["episode"].Value, CultureInfo.InvariantCulture);
                if (value is 480 or 576 or 720 or 1080 or 2160 or 264 or 265)
                {
                    continue;
                }

                return value;
            }
        }

        return null;
    }

    private static string? Join(string? a, string b) => string.IsNullOrEmpty(a) ? b : $"{a}; {b}";

    [GeneratedRegex(@"^(?:season|staffel|saison|temporada|stagione|seizoen|sezon|series|s)[ ._-]*(?<season>\d{1,4})$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolderRegex();

    [GeneratedRegex(@"^(?:specials?|extras|season[ ._-]*0+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialsRegex();

    [GeneratedRegex(@"(?:^|[^a-z])(?:e|ep|episode|episodio|épisode|folge|afl|aflevering)[ ._-]*(?<episode>\d{1,4})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeWordRegex();

    // "Dark - Folge", "Show Episode", "Show - Ep" → "Dark", "Show": what is left when the number was read as the episode.
    [GeneratedRegex(@"[\s\-_.]+(?:e|ep|episode|episodio|épisode|folge|afl|aflevering|part|teil)?[\s\-_.]*$|[\s\-_.]+(?:e|ep|episode|episodio|épisode|folge|afl|aflevering)$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingEpisodeWordRegex();

    [GeneratedRegex(@"^\s*(?<episode>\d{1,4})(?![\dpx])")]
    private static partial Regex LeadingNumberRegex();

    [GeneratedRegex(@"\s-\s*(?<episode>\d{1,4})(?![\dpx])")]
    private static partial Regex DashNumberRegex();

    [GeneratedRegex(@"(?<![\dx])(?<episode>\d{1,3})(?![\dpx])", RegexOptions.IgnoreCase)]
    private static partial Regex AnyNumberRegex();

    [GeneratedRegex(
        @"(?:\.(?:en|de|fr|es|it|nl|pt|sv|no|nb|da|fi|is|pl|ru|ja|zh|ko|ar|he|tr|cs|hu|el|ro|bg|hr|sr|sl|sk|uk|th|vi|id|hi|"
        + @"eng|ger|deu|fre|fra|spa|ita|dut|nld|por|swe|nor|dan|fin|pol|rus|jpn|chi|zho|kor|ara|heb|tur|cze|ces|hun|gre|ell|rum|ron|und)"
        + @"(?:-[a-z]{2,4})?){0,1}(?:\.(?:forced|sdh|cc|hi|default|full))*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SubtitleSuffixRegex();
}
