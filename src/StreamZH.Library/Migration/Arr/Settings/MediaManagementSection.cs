// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using StreamZH.Core.Decisions;
using StreamZH.Library.Import;

namespace StreamZH.Library.Migration.Arr.Settings;

/// <summary>
/// Media Management (/config/mediamanagement) and the download handling switches (/config/downloadclient) → Settings →
/// Media Management: recycle bin, propers, empty folders, file date, rescan, permissions, free space, hardlinks, extra
/// files, unmonitoring deleted files, completed download handling and redownloading failed ones. Only what StreamZH has;
/// paths go through the path mappings.
/// </summary>
public sealed class MediaManagementSection(string source) : ArrSettingsSection(source)
{
    private static readonly string[] ProperNames = ["preferAndUpgrade", "doNotUpgrade", "doNotPrefer"];
    private static readonly string[] RescanNames = ["always", "afterManual", "never"];
    private static readonly string[] RadarrFileDates = ["none", "cinemas", "release"];
    private static readonly string[] SonarrFileDates = ["none", "localAirDate", "utcAirDate"];
    private static readonly string[] EpisodeTitleNames = ["always", "bulkSeasonReleases", "never"];

    public override string Key => "mediaManagement";

    public override string Label => "Media management";

    public override string Description =>
        "Importing and file handling: recycle bin, hardlinks, extra files, permissions, free space, file dates, propers and download handling. Only what StreamZH has.";

    public override int Order => 320;

    protected override async Task<SettingsPlan> PlanAsync(ArrReader reader, CancellationToken cancellationToken)
    {
        var plan = new SettingsPlan();
        var media = await reader.GetAsync("/config/mediamanagement", cancellationToken);
        JsonElement? download = null;
        try
        {
            download = await reader.GetAsync("/config/downloadclient", cancellationToken);
        }
        catch (ArrEndpointMissingException)
        {
            plan.Warnings.Add($"Could not read {reader.AppName}'s download handling settings; StreamZH keeps its own.");
        }

        var planner = new MediaManagementPlanner(plan, reader.Service<MediaManagementSettings>(), Key, IsRadarr ? "Sonarr" : "Radarr", reader.Context.Paths);
        var movies = IsRadarr;

        // Importing
        if (ArrJson.Get(media, "skipFreeSpaceCheckWhenImporting") is { } skip && Flag(skip) is { } skipValue)
        {
            planner.Value("skipFreeSpaceCheck", "Skip free space check", skipValue, v => v.SkipFreeSpaceCheck, (v, x) => v with { SkipFreeSpaceCheck = x }, shared: true);
        }

        if (ArrJson.Int(media, "minimumFreeSpaceWhenImporting") is { } freeSpace)
        {
            planner.Value("minimumFreeSpaceMb", "Minimum free space (MB)", freeSpace, v => v.MinimumFreeSpaceMb, (v, x) => v with { MinimumFreeSpaceMb = x }, shared: true);
        }

        Bool(planner, media, "copyUsingHardlinks", "copyUsingHardlinks", "Use hardlinks instead of copy", v => v.CopyUsingHardlinks, (v, x) => v with { CopyUsingHardlinks = x });
        Bool(planner, media, "importExtraFiles", "importExtraFiles", "Import extra files", v => v.ImportExtraFiles, (v, x) => v with { ImportExtraFiles = x });
        if (ArrJson.Str(media, "extraFileExtensions") is { } extensions)
        {
            var normalized = string.Join(',', new MediaManagementValues { ExtraFileExtensions = extensions }.ExtraExtensions);
            planner.Value("extraFileExtensions", "Extra file extensions", normalized, v => string.Join(',', v.ExtraExtensions), (v, x) => v with { ExtraFileExtensions = x }, shared: true);
        }

        if (!movies && ArrJson.Enum(media, "episodeTitleRequired", EpisodeTitleNames) is { } titleRequired)
        {
            planner.Value("episodeTitleRequired", "Episode title required", (EpisodeTitleRequired)titleRequired, v => v.EpisodeTitleRequired, (v, x) => v with { EpisodeTitleRequired = x });
        }

        if (ArrJson.Bool(media, "useScriptImport"))
        {
            planner.Unsupported("useScriptImport", "Import using script", "StreamZH has no import script; it moves, copies or hardlinks the files itself.", ArrJson.Str(media, "scriptImportPath"));
        }

        // Folders
        Bool(planner, media, movies ? "createEmptyMovieFolders" : "createEmptySeriesFolders", "createEmptyFolders", "Create empty folders", v => v.CreateEmptyFolders, (v, x) => v with { CreateEmptyFolders = x });
        Bool(planner, media, "deleteEmptyFolders", "deleteEmptyFolders", "Delete empty folders", v => v.DeleteEmptyFolders, (v, x) => v with { DeleteEmptyFolders = x });

        // File management
        Bool(
            planner,
            media,
            movies ? "autoUnmonitorPreviouslyDownloadedMovies" : "autoUnmonitorPreviouslyDownloadedEpisodes",
            "unmonitorDeletedFiles",
            "Unmonitor deleted files",
            v => v.UnmonitorDeletedFiles,
            (v, x) => v with { UnmonitorDeletedFiles = x });

        if (ArrJson.Enum(media, "downloadPropersAndRepacks", ProperNames) is { } propers)
        {
            planner.Value("propersAndRepacks", "Propers and repacks", (ProperHandling)propers, v => v.PropersAndRepacks, (v, x) => v with { PropersAndRepacks = x }, shared: true);
        }

        if (ArrJson.Enum(media, "rescanAfterRefresh", RescanNames) is { } rescan)
        {
            planner.Value("rescanAfterRefresh", "Rescan folder after refresh", (RescanAfterRefresh)rescan, v => v.RescanAfterRefresh, (v, x) => v with { RescanAfterRefresh = x }, shared: true);
        }

        if (ArrJson.Enum(media, "fileDate", movies ? RadarrFileDates : SonarrFileDates) is { } fileDate)
        {
            var note = fileDate switch
            {
                2 when movies => "StreamZH sets the cinema release date; Radarr's physical/digital release date is not available.",
                2 => "StreamZH sets the air date in local time; Sonarr's UTC air date is not available.",
                _ => null,
            };
            planner.Value(
                "changeFileDate",
                "Change file date",
                fileDate == 0 ? FileDateMode.None : FileDateMode.ReleaseDate,
                v => v.ChangeFileDate,
                (v, x) => v with { ChangeFileDate = x },
                shared: true,
                note: note);
        }

        if (ArrJson.Str(media, "recycleBin") is { } recycleBin)
        {
            var mapped = recycleBin.Trim().Length == 0 ? string.Empty : reader.Context.Paths.Map(recycleBin.Trim());
            if (mapped.Length > 0)
            {
                reader.Context.Paths.Use(recycleBin.Trim());
            }

            planner.Value("recycleBin", "Recycle bin", mapped, v => v.RecycleBin, (v, x) => v with { RecycleBin = x }, shared: true);
        }

        if (ArrJson.Int(media, "recycleBinCleanupDays") is { } cleanup)
        {
            planner.Value("recycleBinCleanupDays", "Recycle bin cleanup (days)", cleanup, v => v.RecycleBinCleanupDays, (v, x) => v with { RecycleBinCleanupDays = x }, shared: true);
        }

        // Permissions
        Bool(planner, media, "setPermissionsLinux", "setPermissions", "Set permissions", v => v.SetPermissions, (v, x) => v with { SetPermissions = x });
        if (ArrJson.Str(media, "chmodFolder")?.Trim() is { Length: > 0 } chmod)
        {
            planner.Value("chmodFolder", "chmod folder", chmod, v => v.ChmodFolder, (v, x) => v with { ChmodFolder = x }, shared: true);
        }

        if (ArrJson.Str(media, "chownGroup") is { } chown)
        {
            planner.Value("chownGroup", "chown group", chown.Trim(), v => v.ChownGroup, (v, x) => v with { ChownGroup = x }, shared: true);
        }

        // Completed and failed download handling
        if (download is { } d)
        {
            Bool(planner, d, "enableCompletedDownloadHandling", "completedDownloadHandling", "Completed download handling", v => v.CompletedDownloadHandling, (v, x) => v with { CompletedDownloadHandling = x });
            Bool(planner, d, "autoRedownloadFailed", "redownloadFailed", "Redownload failed downloads", v => v.RedownloadFailed, (v, x) => v with { RedownloadFailed = x });
        }

        return plan;
    }

    private static bool? Flag(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static void Bool(
        MediaManagementPlanner planner,
        JsonElement config,
        string jsonName,
        string field,
        string label,
        Func<MediaManagementValues, bool> get,
        Func<MediaManagementValues, bool, MediaManagementValues> set)
    {
        if (ArrJson.Get(config, jsonName) is { } value && Flag(value) is { } flag)
        {
            // Every Media Management value is one for movies and series in StreamZH.
            planner.Value(field, label, flag, get, set, shared: true);
        }
    }
}
