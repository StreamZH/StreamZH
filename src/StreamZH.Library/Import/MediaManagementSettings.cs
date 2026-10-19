// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using StreamZH.Core.Decisions;
using StreamZH.Data;

namespace StreamZH.Library.Import;

/// <summary>JSON as camelCase strings ("spaceDashSpace"), like the other settings.</summary>
public sealed class CamelCaseEnumConverter<T>() : JsonStringEnumConverter<T>(JsonNamingPolicy.CamelCase)
    where T : struct, Enum;

/// <summary>What a colon in a title becomes in file and folder names (Radarr/Sonarr "Colon Replacement").</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ColonReplacement>))]
public enum ColonReplacement
{
    Delete,
    Dash,
    SpaceDash,
    SpaceDashSpace,

    /// <summary>"Title: Subtitle" → "Title - Subtitle", "12:30" → "12-30".</summary>
    Smart,
}

/// <summary>How a file with several episodes is named (Sonarr "Multi-Episode Style").</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<MultiEpisodeStyle>))]
public enum MultiEpisodeStyle
{
    /// <summary>S01E01-02-03</summary>
    Extend,

    /// <summary>S01E01.S01E02</summary>
    Duplicate,

    /// <summary>S01E01E02E03</summary>
    Repeat,

    /// <summary>S01E01-E02-E03</summary>
    Scene,

    /// <summary>S01E01-03</summary>
    Range,

    /// <summary>S01E01-E03</summary>
    PrefixedRange,
}

/// <summary>What happens to the files of a finished download when they are imported.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ImportMode>))]
public enum ImportMode
{
    /// <summary>Moved into the library (default; the download folder is removed afterwards).</summary>
    Move,

    /// <summary>Copied (or hard-linked when possible), the download stays where it is.</summary>
    Copy,
}

/// <summary>Sonarr's "Episode Title Required": hold an import back while the episode title is not known yet.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<EpisodeTitleRequired>))]
public enum EpisodeTitleRequired
{
    Always,
    BulkSeasonReleases,
    Never,
}

/// <summary>Whether the folder is scanned again after a metadata refresh.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<RescanAfterRefresh>))]
public enum RescanAfterRefresh
{
    Always,
    AfterManualRefresh,
    Never,
}

/// <summary>"Change File Date": the modification date of imported files.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<FileDateMode>))]
public enum FileDateMode
{
    /// <summary>The date stays as it is.</summary>
    None,

    /// <summary>Movies: the release date (in cinemas) from TMDB; episodes: the air date.</summary>
    ReleaseDate,
}

/// <summary>All Media Management settings. Defaults keep StreamZH's behaviour and follow Radarr/Sonarr where they apply.</summary>
public record MediaManagementValues
{
    public const string DefaultMovieFormat = "{Movie Title} ({Release Year}) {Quality Full}";
    public const string DefaultMovieFolderFormat = "{Movie Title} ({Release Year})";
    public const string DefaultEpisodeFormat = "{Series Title} - S{season:00}E{episode:00} - {Episode Title} {Quality Full}";
    public const string DefaultDailyFormat = "{Series Title} - {Air-Date} - {Episode Title} {Quality Full}";
    public const string DefaultAnimeFormat = "{Series Title} - S{season:00}E{episode:00} - {Episode Title} {Quality Full}";
    public const string DefaultSeriesFolderFormat = "{Series TitleYear}";
    public const string DefaultSeasonFolderFormat = "Season {season}";
    public const string DefaultSpecialsFolderFormat = "Specials";
    public const string DefaultExtraFileExtensions = "srt,ass,ssa,sub,idx";

    // Movie naming
    public bool RenameMovies { get; init; } = true;
    public bool ReplaceIllegalCharacters { get; init; } = true;
    public ColonReplacement ColonReplacement { get; init; } = ColonReplacement.Smart;
    public string StandardMovieFormat { get; init; } = DefaultMovieFormat;
    public string MovieFolderFormat { get; init; } = DefaultMovieFolderFormat;

    // Episode naming
    public bool RenameEpisodes { get; init; } = true;
    public string StandardEpisodeFormat { get; init; } = DefaultEpisodeFormat;
    public string DailyEpisodeFormat { get; init; } = DefaultDailyFormat;
    public string AnimeEpisodeFormat { get; init; } = DefaultAnimeFormat;
    public string SeriesFolderFormat { get; init; } = DefaultSeriesFolderFormat;
    public bool UseSeasonFolders { get; init; } = true;
    public string SeasonFolderFormat { get; init; } = DefaultSeasonFolderFormat;
    public string SpecialsFolderFormat { get; init; } = DefaultSpecialsFolderFormat;
    public MultiEpisodeStyle MultiEpisodeStyle { get; init; } = MultiEpisodeStyle.PrefixedRange;

    // Folders
    public bool CreateEmptyFolders { get; init; }
    public bool DeleteEmptyFolders { get; init; }

    // Importing
    public bool SkipFreeSpaceCheck { get; init; }
    public int MinimumFreeSpaceMb { get; init; } = 100;
    public ImportMode ImportMode { get; init; } = ImportMode.Move;
    public bool CopyUsingHardlinks { get; init; } = true;
    public bool ImportExtraFiles { get; init; }
    public string ExtraFileExtensions { get; init; } = DefaultExtraFileExtensions;
    public EpisodeTitleRequired EpisodeTitleRequired { get; init; } = EpisodeTitleRequired.Always;

    // File management
    [JsonConverter(typeof(CamelCaseEnumConverter<ProperHandling>))]
    public ProperHandling PropersAndRepacks { get; init; } = ProperHandling.PreferAndUpgrade;
    public RescanAfterRefresh RescanAfterRefresh { get; init; } = RescanAfterRefresh.Always;
    public FileDateMode ChangeFileDate { get; init; } = FileDateMode.None;
    public string RecycleBin { get; init; } = string.Empty;
    public int RecycleBinCleanupDays { get; init; } = 7;
    public bool UnmonitorDeletedFiles { get; init; }

    // Permissions (Linux and macOS)
    public bool SetPermissions { get; init; }
    public string ChmodFolder { get; init; } = "755";
    public string ChownGroup { get; init; } = string.Empty;

    // Completed and failed download handling
    public bool CompletedDownloadHandling { get; init; } = true;
    public bool RemoveCompletedDownloads { get; init; }
    public bool RedownloadFailed { get; init; } = true;
    public bool RemoveFailedDownloads { get; init; } = true;

    /// <summary>The extra file extensions as a set, without dots, lower-case.</summary>
    [JsonIgnore]
    public IReadOnlySet<string> ExtraExtensions =>
        ExtraFileExtensions.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.TrimStart('.').ToLowerInvariant())
            .Where(e => e.Length > 0)
            .ToHashSet();
}

/// <summary>
/// Settings → Media Management, stored in the Config table (one key per setting, empty = default) and cached; read by
/// the import, the naming and the library. <see cref="Update"/> checks everything first and saves nothing when a value is invalid.
/// </summary>
/// <param name="paths">0.12 (V-02): the recycle bin may not be inside the data folder (outside downloads/) or contain it.</param>
public sealed partial class MediaManagementSettings(ConfigRepository config, StreamZH.Core.AppPaths? paths = null)
{
    public const int MaxMinimumFreeSpaceMb = 1_000_000;
    public const int MaxRecycleBinCleanupDays = 3650;

    private MediaManagementValues? _cached;

    public MediaManagementValues Current => _cached ??= Load();

    /// <exception cref="ValidationFailedException">Keys are the camelCase property names.</exception>
    public MediaManagementValues Update(MediaManagementValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var errors = Validate(values);
        if (!errors.ContainsKey("recycleBin") && paths?.FolderProblem(values.RecycleBin.Trim()) is { } binProblem)
        {
            errors["recycleBin"] = [binProblem];
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        var recycleBin = values.RecycleBin.Trim();
        var normalized = values with
        {
            RecycleBin = recycleBin.Length == 0 ? string.Empty : Path.TrimEndingDirectorySeparator(Path.GetFullPath(recycleBin)),
            ExtraFileExtensions = string.Join(',', values.ExtraExtensions),
            ChownGroup = values.ChownGroup.Trim(),
            ChmodFolder = values.ChmodFolder.Trim(),
            StandardMovieFormat = values.StandardMovieFormat.Trim(),
            MovieFolderFormat = values.MovieFolderFormat.Trim(),
            StandardEpisodeFormat = values.StandardEpisodeFormat.Trim(),
            DailyEpisodeFormat = values.DailyEpisodeFormat.Trim(),
            AnimeEpisodeFormat = values.AnimeEpisodeFormat.Trim(),
            SeriesFolderFormat = values.SeriesFolderFormat.Trim(),
            SeasonFolderFormat = values.SeasonFolderFormat.Trim(),
            SpecialsFolderFormat = values.SpecialsFolderFormat.Trim(),
        };

        Set(ConfigKeys.RenameMovies, normalized.RenameMovies);
        Set(ConfigKeys.ReplaceIllegalCharacters, normalized.ReplaceIllegalCharacters);
        Set(ConfigKeys.ColonReplacement, normalized.ColonReplacement);
        config.Set(ConfigKeys.StandardMovieFormat, normalized.StandardMovieFormat);
        config.Set(ConfigKeys.MovieFolderFormat, normalized.MovieFolderFormat);
        Set(ConfigKeys.RenameEpisodes, normalized.RenameEpisodes);
        config.Set(ConfigKeys.StandardEpisodeFormat, normalized.StandardEpisodeFormat);
        config.Set(ConfigKeys.DailyEpisodeFormat, normalized.DailyEpisodeFormat);
        config.Set(ConfigKeys.AnimeEpisodeFormat, normalized.AnimeEpisodeFormat);
        config.Set(ConfigKeys.SeriesFolderFormat, normalized.SeriesFolderFormat);
        Set(ConfigKeys.UseSeasonFolders, normalized.UseSeasonFolders);
        config.Set(ConfigKeys.SeasonFolderFormat, normalized.SeasonFolderFormat);
        config.Set(ConfigKeys.SpecialsFolderFormat, normalized.SpecialsFolderFormat);
        Set(ConfigKeys.MultiEpisodeStyle, normalized.MultiEpisodeStyle);
        Set(ConfigKeys.CreateEmptyFolders, normalized.CreateEmptyFolders);
        Set(ConfigKeys.DeleteEmptyFolders, normalized.DeleteEmptyFolders);
        Set(ConfigKeys.SkipFreeSpaceCheck, normalized.SkipFreeSpaceCheck);
        config.Set(ConfigKeys.MinimumFreeSpaceMb, normalized.MinimumFreeSpaceMb.ToString(CultureInfo.InvariantCulture));
        Set(ConfigKeys.ImportMode, normalized.ImportMode);
        Set(ConfigKeys.CopyUsingHardlinks, normalized.CopyUsingHardlinks);
        Set(ConfigKeys.ImportExtraFiles, normalized.ImportExtraFiles);
        config.Set(ConfigKeys.ExtraFileExtensions, normalized.ExtraFileExtensions);
        Set(ConfigKeys.EpisodeTitleRequired, normalized.EpisodeTitleRequired);
        Set(ConfigKeys.PropersAndRepacks, normalized.PropersAndRepacks);
        Set(ConfigKeys.RescanAfterRefresh, normalized.RescanAfterRefresh);
        Set(ConfigKeys.ChangeFileDate, normalized.ChangeFileDate);
        config.Set(ConfigKeys.RecycleBin, normalized.RecycleBin);
        config.Set(ConfigKeys.RecycleBinCleanupDays, normalized.RecycleBinCleanupDays.ToString(CultureInfo.InvariantCulture));
        Set(ConfigKeys.UnmonitorDeletedFiles, normalized.UnmonitorDeletedFiles);
        Set(ConfigKeys.SetPermissions, normalized.SetPermissions);
        config.Set(ConfigKeys.ChmodFolder, normalized.ChmodFolder);
        config.Set(ConfigKeys.ChownGroup, normalized.ChownGroup);
        Set(ConfigKeys.CompletedDownloadHandling, normalized.CompletedDownloadHandling);
        Set(ConfigKeys.RemoveCompletedDownloads, normalized.RemoveCompletedDownloads);
        Set(ConfigKeys.RedownloadFailed, normalized.RedownloadFailed);
        Set(ConfigKeys.RemoveFailedDownloads, normalized.RemoveFailedDownloads);

        _cached = null;
        return Current;
    }

    /// <summary>Field errors (camelCase keys); empty when everything is fine.</summary>
    public static Dictionary<string, string[]> Validate(MediaManagementValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var errors = new Dictionary<string, string[]>();
        void Check(string field, string? problem)
        {
            if (problem is not null)
            {
                errors[field] = [problem];
            }
        }

        Check("standardMovieFormat", FileNameBuilder.ValidateFormat(values.StandardMovieFormat, NamingFormatKind.MovieFile));
        Check("movieFolderFormat", FileNameBuilder.ValidateFormat(values.MovieFolderFormat, NamingFormatKind.MovieFolder));
        Check("standardEpisodeFormat", FileNameBuilder.ValidateFormat(values.StandardEpisodeFormat, NamingFormatKind.StandardEpisode));
        Check("dailyEpisodeFormat", FileNameBuilder.ValidateFormat(values.DailyEpisodeFormat, NamingFormatKind.DailyEpisode));
        Check("animeEpisodeFormat", FileNameBuilder.ValidateFormat(values.AnimeEpisodeFormat, NamingFormatKind.AnimeEpisode));
        Check("seriesFolderFormat", FileNameBuilder.ValidateFormat(values.SeriesFolderFormat, NamingFormatKind.SeriesFolder));
        Check("seasonFolderFormat", FileNameBuilder.ValidateFormat(values.SeasonFolderFormat, NamingFormatKind.SeasonFolder));
        Check("specialsFolderFormat", FileNameBuilder.ValidateFormat(values.SpecialsFolderFormat, NamingFormatKind.SpecialsFolder));

        if (values.MinimumFreeSpaceMb is < 0 or > MaxMinimumFreeSpaceMb)
        {
            Check("minimumFreeSpaceMb", $"Use 0 to {MaxMinimumFreeSpaceMb.ToString("N0", CultureInfo.InvariantCulture)} MB.");
        }

        var extensions = values.ExtraExtensions;
        if (extensions.Any(e => !ExtensionRegex().IsMatch(e)))
        {
            Check("extraFileExtensions", "Use extensions separated by commas, e.g. srt,ass,sub,idx,nfo.");
        }
        else if (extensions.FirstOrDefault(e => DiskScanner.VideoExtensions.Contains("." + e)) is { } video)
        {
            Check("extraFileExtensions", $"{video} is a video file; video files are always imported.");
        }
        else if (extensions.Count > 30)
        {
            Check("extraFileExtensions", "Use at most 30 extensions.");
        }

        var bin = values.RecycleBin.Trim();
        if (bin.Length > 0)
        {
            if (!Path.IsPathFullyQualified(bin))
            {
                Check("recycleBin", "Enter a full path, e.g. /media/.recycle or D:\\Recycle, or leave it empty to delete files.");
            }
            else if (Search.DownloadSettings.CheckWritable(Path.GetFullPath(bin)) is { } problem)
            {
                Check("recycleBin", problem);
            }
        }

        if (values.RecycleBinCleanupDays is < 0 or > MaxRecycleBinCleanupDays)
        {
            Check("recycleBinCleanupDays", $"Use 0 (never clean up) to {MaxRecycleBinCleanupDays} days.");
        }

        if (!ChmodRegex().IsMatch(values.ChmodFolder.Trim()))
        {
            Check("chmodFolder", "Use an octal mode like 755 or 775.");
        }

        var group = values.ChownGroup.Trim();
        if (group.Length > 0 && !GroupRegex().IsMatch(group))
        {
            Check("chownGroup", "Use a group name or a numeric group id.");
        }

        return errors;
    }

    private MediaManagementValues Load()
    {
        var d = new MediaManagementValues();
        return new MediaManagementValues
        {
            RenameMovies = Bool(ConfigKeys.RenameMovies, d.RenameMovies),
            ReplaceIllegalCharacters = Bool(ConfigKeys.ReplaceIllegalCharacters, d.ReplaceIllegalCharacters),
            ColonReplacement = Enum(ConfigKeys.ColonReplacement, d.ColonReplacement),
            StandardMovieFormat = Text(ConfigKeys.StandardMovieFormat, d.StandardMovieFormat),
            MovieFolderFormat = Text(ConfigKeys.MovieFolderFormat, d.MovieFolderFormat),
            RenameEpisodes = Bool(ConfigKeys.RenameEpisodes, d.RenameEpisodes),
            StandardEpisodeFormat = Text(ConfigKeys.StandardEpisodeFormat, d.StandardEpisodeFormat),
            DailyEpisodeFormat = Text(ConfigKeys.DailyEpisodeFormat, d.DailyEpisodeFormat),
            AnimeEpisodeFormat = Text(ConfigKeys.AnimeEpisodeFormat, d.AnimeEpisodeFormat),
            SeriesFolderFormat = Text(ConfigKeys.SeriesFolderFormat, d.SeriesFolderFormat),
            UseSeasonFolders = Bool(ConfigKeys.UseSeasonFolders, d.UseSeasonFolders),
            SeasonFolderFormat = Text(ConfigKeys.SeasonFolderFormat, d.SeasonFolderFormat),
            SpecialsFolderFormat = Text(ConfigKeys.SpecialsFolderFormat, d.SpecialsFolderFormat),
            MultiEpisodeStyle = Enum(ConfigKeys.MultiEpisodeStyle, d.MultiEpisodeStyle),
            CreateEmptyFolders = Bool(ConfigKeys.CreateEmptyFolders, d.CreateEmptyFolders),
            DeleteEmptyFolders = Bool(ConfigKeys.DeleteEmptyFolders, d.DeleteEmptyFolders),
            SkipFreeSpaceCheck = Bool(ConfigKeys.SkipFreeSpaceCheck, d.SkipFreeSpaceCheck),
            MinimumFreeSpaceMb = Int(ConfigKeys.MinimumFreeSpaceMb, d.MinimumFreeSpaceMb, 0, MaxMinimumFreeSpaceMb),
            ImportMode = Enum(ConfigKeys.ImportMode, d.ImportMode),
            CopyUsingHardlinks = Bool(ConfigKeys.CopyUsingHardlinks, d.CopyUsingHardlinks),
            ImportExtraFiles = Bool(ConfigKeys.ImportExtraFiles, d.ImportExtraFiles),
            ExtraFileExtensions = config.Get(ConfigKeys.ExtraFileExtensions) ?? d.ExtraFileExtensions,
            EpisodeTitleRequired = Enum(ConfigKeys.EpisodeTitleRequired, d.EpisodeTitleRequired),
            PropersAndRepacks = Enum(ConfigKeys.PropersAndRepacks, d.PropersAndRepacks),
            RescanAfterRefresh = Enum(ConfigKeys.RescanAfterRefresh, d.RescanAfterRefresh),
            ChangeFileDate = Enum(ConfigKeys.ChangeFileDate, d.ChangeFileDate),
            RecycleBin = config.Get(ConfigKeys.RecycleBin) ?? d.RecycleBin,
            RecycleBinCleanupDays = Int(ConfigKeys.RecycleBinCleanupDays, d.RecycleBinCleanupDays, 0, MaxRecycleBinCleanupDays),
            UnmonitorDeletedFiles = Bool(ConfigKeys.UnmonitorDeletedFiles, d.UnmonitorDeletedFiles),
            SetPermissions = Bool(ConfigKeys.SetPermissions, d.SetPermissions),
            ChmodFolder = Text(ConfigKeys.ChmodFolder, d.ChmodFolder),
            ChownGroup = config.Get(ConfigKeys.ChownGroup) ?? d.ChownGroup,
            CompletedDownloadHandling = Bool(ConfigKeys.CompletedDownloadHandling, d.CompletedDownloadHandling),
            RemoveCompletedDownloads = Bool(ConfigKeys.RemoveCompletedDownloads, d.RemoveCompletedDownloads),
            RedownloadFailed = Bool(ConfigKeys.RedownloadFailed, d.RedownloadFailed),
            RemoveFailedDownloads = Bool(ConfigKeys.RemoveFailedDownloads, d.RemoveFailedDownloads),
        };
    }

    private void Set(string key, bool value) => config.Set(key, value ? "true" : "false");

    private void Set<T>(string key, T value)
        where T : struct, Enum => config.Set(key, JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));

    private bool Bool(string key, bool fallback) => config.Get(key) switch
    {
        "true" => true,
        "false" => false,
        _ => fallback,
    };

    private string Text(string key, string fallback) => config.Get(key) is { Length: > 0 } value ? value : fallback;

    private int Int(string key, int fallback, int min, int max) =>
        int.TryParse(config.Get(key), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max ? value : fallback;

    private T Enum<T>(string key, T fallback)
        where T : struct, Enum => System.Enum.TryParse<T>(config.Get(key), ignoreCase: true, out var value) && System.Enum.IsDefined(value) ? value : fallback;

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,15}$")]
    private static partial Regex ExtensionRegex();

    [GeneratedRegex("^[0-7]{3,4}$")]
    private static partial Regex ChmodRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex GroupRegex();
}
