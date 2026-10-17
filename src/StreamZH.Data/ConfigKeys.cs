// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Data;

public static class ConfigKeys
{
    public const string ApiKey = "ApiKey";
    public const string InstanceName = "InstanceName";
    public const string LogLevel = "LogLevel";
    public const string TmdbToken = "TmdbToken";
    public const string TmdbLanguage = "TmdbLanguage";
    public const string DefaultQualityProfileId = "DefaultQualityProfileId";
    public const string NzbFolder = "NzbFolder";
    public const string Par2Path = "Par2Path";
    public const string SevenZipPath = "SevenZipPath";
    public const string ToolsAutoInstall = "ToolsAutoInstall";
    public const string DownloadClient = "DownloadClient";
    public const string IncompleteFolder = "IncompleteFolder";
    public const string CompleteFolder = "CompleteFolder";
    public const string DownloadSpeedLimit = "DownloadSpeedLimit";
    public const string DownloadsPaused = "DownloadsPaused";

    // Download engine expert settings (Settings → Downloads, advanced); empty = default.
    public const string DownloadMinFreeSpaceGb = "DownloadMinFreeSpaceGb";
    public const string DownloadKeepFiles = "DownloadKeepFiles";
    public const string DownloadAbortHopeless = "DownloadAbortHopeless";
    public const string DownloadArticleTries = "DownloadArticleTries";

    // Media Management (0.6): naming, importing, file management, permissions, completed/failed download handling.
    public const string RenameMovies = "RenameMovies";
    public const string RenameEpisodes = "RenameEpisodes";
    public const string ReplaceIllegalCharacters = "ReplaceIllegalCharacters";
    public const string ColonReplacement = "ColonReplacement";
    public const string StandardMovieFormat = "StandardMovieFormat";
    public const string MovieFolderFormat = "MovieFolderFormat";
    public const string StandardEpisodeFormat = "StandardEpisodeFormat";
    public const string DailyEpisodeFormat = "DailyEpisodeFormat";
    public const string AnimeEpisodeFormat = "AnimeEpisodeFormat";
    public const string SeriesFolderFormat = "SeriesFolderFormat";
    public const string SeasonFolderFormat = "SeasonFolderFormat";
    public const string SpecialsFolderFormat = "SpecialsFolderFormat";
    public const string UseSeasonFolders = "UseSeasonFolders";
    public const string MultiEpisodeStyle = "MultiEpisodeStyle";
    public const string CreateEmptyFolders = "CreateEmptyFolders";
    public const string DeleteEmptyFolders = "DeleteEmptyFolders";
    public const string SkipFreeSpaceCheck = "SkipFreeSpaceCheck";
    public const string MinimumFreeSpaceMb = "MinimumFreeSpaceMb";
    public const string ImportMode = "ImportMode";
    public const string CopyUsingHardlinks = "CopyUsingHardlinks";
    public const string ImportExtraFiles = "ImportExtraFiles";
    public const string ExtraFileExtensions = "ExtraFileExtensions";
    public const string EpisodeTitleRequired = "EpisodeTitleRequired";
    public const string PropersAndRepacks = "PropersAndRepacks";
    public const string RescanAfterRefresh = "RescanAfterRefresh";
    public const string ChangeFileDate = "ChangeFileDate";
    public const string RecycleBin = "RecycleBin";
    public const string RecycleBinCleanupDays = "RecycleBinCleanupDays";
    public const string UnmonitorDeletedFiles = "UnmonitorDeletedFiles";
    public const string SetPermissions = "SetPermissions";
    public const string ChmodFolder = "ChmodFolder";
    public const string ChownGroup = "ChownGroup";
    public const string CompletedDownloadHandling = "CompletedDownloadHandling";
    public const string RemoveCompletedDownloads = "RemoveCompletedDownloads";
    public const string RedownloadFailed = "RedownloadFailed";
    public const string RemoveFailedDownloads = "RemoveFailedDownloads";
}
