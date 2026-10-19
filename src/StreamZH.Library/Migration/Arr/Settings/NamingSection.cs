// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using System.Text.RegularExpressions;
using StreamZH.Library.Import;

namespace StreamZH.Library.Migration.Arr.Settings;

/// <summary>
/// Naming (/config/naming) → Settings → Media Management. StreamZH's tokens are Radarr's and Sonarr's, so formats carry
/// over as they are; Sonarr v3's {Preferred Words} becomes {Custom Formats} (what Sonarr v4 did). A format StreamZH's
/// token check rejects keeps StreamZH's format, with a warning naming the problem.
/// </summary>
public sealed partial class NamingSection(string source) : ArrSettingsSection(source)
{
    private static readonly string[] ColonNames = ["delete", "dash", "spaceDash", "spaceDashSpace", "smart", "custom"];
    private static readonly string[] MultiEpisodeNames = ["extend", "duplicate", "repeat", "scene", "range", "prefixedRange"];

    public override string Key => "naming";

    public override string Label => "Naming";

    public override string Description => IsRadarr
        ? "Movie file and folder names, renaming and colon replacement (Settings → Media Management)."
        : "Episode file names (standard, daily, anime), series, season and specials folders, renaming and multi-episode style.";

    public override int Order => 300;

    protected override async Task<SettingsPlan> PlanAsync(ArrReader reader, CancellationToken cancellationToken)
    {
        var plan = new SettingsPlan();
        var naming = await reader.GetAsync("/config/naming", cancellationToken);
        var planner = new MediaManagementPlanner(plan, reader.Service<MediaManagementSettings>(), Key, IsRadarr ? "Sonarr" : "Radarr", reader.Context.Paths);

        if (IsRadarr)
        {
            Bool(planner, naming, "renameMovies", "Rename movies", v => v.RenameMovies, (v, x) => v with { RenameMovies = x });
        }
        else
        {
            Bool(planner, naming, "renameEpisodes", "Rename episodes", v => v.RenameEpisodes, (v, x) => v with { RenameEpisodes = x });
        }

        Bool(planner, naming, "replaceIllegalCharacters", "Replace illegal characters", v => v.ReplaceIllegalCharacters, (v, x) => v with { ReplaceIllegalCharacters = x }, shared: true);

        if (ArrJson.Enum(naming, "colonReplacementFormat", ColonNames) is { } colon)
        {
            if (colon == 5)
            {
                planner.Unsupported(
                    "colonReplacement",
                    "Colon replacement",
                    "StreamZH has no custom colon replacement.",
                    ArrJson.Str(naming, "customColonReplacementFormat"),
                    $"Sonarr replaces colons with your own text; StreamZH has no custom replacement and keeps {MediaManagementPlanner.Show(planner.Current.ColonReplacement)}.");
            }
            else
            {
                planner.Value("colonReplacement", "Colon replacement", (ColonReplacement)colon, v => v.ColonReplacement, (v, x) => v with { ColonReplacement = x }, shared: true);
            }
        }

        if (IsRadarr)
        {
            Format(planner, naming, "standardMovieFormat", "Movie file name", NamingFormatKind.MovieFile, v => v.StandardMovieFormat, (v, x) => v with { StandardMovieFormat = x });
            Format(planner, naming, "movieFolderFormat", "Movie folder name", NamingFormatKind.MovieFolder, v => v.MovieFolderFormat, (v, x) => v with { MovieFolderFormat = x });
            return plan;
        }

        if (ArrJson.Enum(naming, "multiEpisodeStyle", MultiEpisodeNames) is { } style)
        {
            planner.Value("multiEpisodeStyle", "Multi-episode style", (MultiEpisodeStyle)style, v => v.MultiEpisodeStyle, (v, x) => v with { MultiEpisodeStyle = x });
        }

        Format(planner, naming, "standardEpisodeFormat", "Episode file name", NamingFormatKind.StandardEpisode, v => v.StandardEpisodeFormat, (v, x) => v with { StandardEpisodeFormat = x });
        Format(planner, naming, "dailyEpisodeFormat", "Daily episode file name", NamingFormatKind.DailyEpisode, v => v.DailyEpisodeFormat, (v, x) => v with { DailyEpisodeFormat = x });
        Format(planner, naming, "animeEpisodeFormat", "Anime episode file name", NamingFormatKind.AnimeEpisode, v => v.AnimeEpisodeFormat, (v, x) => v with { AnimeEpisodeFormat = x });
        Format(planner, naming, "seriesFolderFormat", "Series folder name", NamingFormatKind.SeriesFolder, v => v.SeriesFolderFormat, (v, x) => v with { SeriesFolderFormat = x });
        Format(planner, naming, "seasonFolderFormat", "Season folder name", NamingFormatKind.SeasonFolder, v => v.SeasonFolderFormat, (v, x) => v with { SeasonFolderFormat = x });
        Format(planner, naming, "specialsFolderFormat", "Specials folder name", NamingFormatKind.SpecialsFolder, v => v.SpecialsFolderFormat, (v, x) => v with { SpecialsFolderFormat = x });
        return plan;
    }

    /// <summary>
    /// A Radarr/Sonarr format in StreamZH's tokens: {Preferred Words} (Sonarr v3) becomes {Custom Formats}, keeping
    /// prefix and suffix ("{[Preferred Words]}" → "{[Custom Formats]}"). The note says what changed, null when nothing did.
    /// </summary>
    public static (string Format, string? Note) Translate(string format)
    {
        ArgumentNullException.ThrowIfNull(format);
        var translated = PreferredWordsRegex().Replace(format, m => $"{{{m.Groups["prefix"].Value}Custom{m.Groups["sep"].Value}Formats{m.Groups["suffix"].Value}}}");
        return translated == format ? (format, null) : (translated, "{Preferred Words} became {Custom Formats}, like in Sonarr v4.");
    }

    private static void Bool(
        MediaManagementPlanner planner,
        JsonElement naming,
        string name,
        string label,
        Func<MediaManagementValues, bool> get,
        Func<MediaManagementValues, bool, MediaManagementValues> set,
        bool shared = false)
    {
        if (ArrJson.Get(naming, name) is { ValueKind: JsonValueKind.True or JsonValueKind.False } value)
        {
            planner.Value(name, label, value.GetBoolean(), get, set, shared);
        }
    }

    private static void Format(
        MediaManagementPlanner planner,
        JsonElement naming,
        string name,
        string label,
        NamingFormatKind kind,
        Func<MediaManagementValues, string> get,
        Func<MediaManagementValues, string, MediaManagementValues> set)
    {
        if (ArrJson.Str(naming, name)?.Trim() is not { Length: > 0 } format)
        {
            return;
        }

        var (translated, note) = Translate(format);
        if (FileNameBuilder.ValidateFormat(translated, kind) is { } problem)
        {
            planner.Unsupported(
                name,
                label,
                "StreamZH cannot use this format.",
                format,
                $"{label} \"{format}\": {problem} StreamZH keeps \"{get(planner.Current)}\".");
            return;
        }

        planner.Value(name, label, translated, get, set, note: note);
    }

    [GeneratedRegex(@"\{(?<prefix>[- ._\[(]*)Preferred(?<sep>[- ._]+)Words(?<suffix>[- ._)\]]*)\}", RegexOptions.IgnoreCase)]
    private static partial Regex PreferredWordsRegex();
}
