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

    // Indexers (0.6): release rules for all indexers and automation (Settings → Indexers); empty = default.
    public const string IndexerMinimumAge = "IndexerMinimumAge";
    public const string IndexerRetention = "IndexerRetention";
    public const string IndexerMaximumSize = "IndexerMaximumSize";
    public const string IndexerAllowHardcodedSubs = "IndexerAllowHardcodedSubs";
    public const string IndexerWhitelistedHardcodedSubs = "IndexerWhitelistedHardcodedSubs";
    public const string IndexerMinimumAvailability = "IndexerMinimumAvailability";
    public const string IndexerAvailabilityDelay = "IndexerAvailabilityDelay";
    public const string IndexerRssSyncInterval = "IndexerRssSyncInterval";
    public const string IndexerMissingSearchInterval = "IndexerMissingSearchInterval";
    public const string IndexerSearchOnAdd = "IndexerSearchOnAdd";

    // Downloads (0.6): SABnzbd-style folders, queue and post-processing switches, history, special; empty = default.
    public const string DownloadNzbBackupFolder = "DownloadNzbBackupFolder";
    public const string DownloadWatchFolder = "DownloadWatchFolder";
    public const string DownloadWatchInterval = "DownloadWatchInterval";
    public const string DownloadPauseOnStart = "DownloadPauseOnStart";
    public const string DownloadPreCheck = "DownloadPreCheck";
    public const string DownloadMaxActive = "DownloadMaxActive";
    public const string DownloadPropagationDelay = "DownloadPropagationDelay";
    public const string DownloadDuplicateAction = "DownloadDuplicateAction";
    public const string DownloadAutoSort = "DownloadAutoSort";
    public const string DownloadUnwantedExtensions = "DownloadUnwantedExtensions";
    public const string DownloadUnwantedAction = "DownloadUnwantedAction";
    public const string DownloadEncryptedAction = "DownloadEncryptedAction";
    public const string DownloadAllPar2 = "DownloadAllPar2";
    public const string DownloadOnlyVerified = "DownloadOnlyVerified";
    public const string DownloadRecursiveUnpack = "DownloadRecursiveUnpack";
    public const string DownloadIgnoreSamples = "DownloadIgnoreSamples";
    public const string DownloadCleanupList = "DownloadCleanupList";
    public const string DownloadDeobfuscate = "DownloadDeobfuscate";
    public const string DownloadTemporaryFolderName = "DownloadTemporaryFolderName";
    public const string DownloadPar2Arguments = "DownloadPar2Arguments";
    public const string DownloadSevenZipArguments = "DownloadSevenZipArguments";
    public const string DownloadToolPriority = "DownloadToolPriority";
    public const string DownloadPostProcessWorkers = "DownloadPostProcessWorkers";
    public const string DownloadMaxLineSpeed = "DownloadMaxLineSpeed";
    public const string DownloadHistoryKeepDays = "DownloadHistoryKeepDays";
    public const string DownloadHistoryKeepItems = "DownloadHistoryKeepItems";
    public const string DownloadHistoryKeepFailed = "DownloadHistoryKeepFailed";
    public const string DownloadPausedCategories = "DownloadPausedCategories";
    public const string DownloadPausedBySchedule = "DownloadPausedBySchedule";

    // Metadata (0.6): NFO options, image cache and the Kodi (XBMC) / Emby metadata writer; empty = default.
    public const string MetadataCertificationCountry = "MetadataCertificationCountry";
    public const string MetadataNfoOriginalTitle = "MetadataNfoOriginalTitle";
    public const string MetadataImageCache = "MetadataImageCache";
    public const string KodiMetadataEnabled = "KodiMetadataEnabled";
    public const string KodiMovieMetadata = "KodiMovieMetadata";
    public const string KodiMovieMetadataUrl = "KodiMovieMetadataUrl";
    public const string KodiMovieImages = "KodiMovieImages";
    public const string KodiUseMovieNfo = "KodiUseMovieNfo";
    public const string KodiMovieCollection = "KodiMovieCollection";
    public const string KodiSeriesMetadata = "KodiSeriesMetadata";
    public const string KodiSeriesMetadataUrl = "KodiSeriesMetadataUrl";
    public const string KodiEpisodeMetadata = "KodiEpisodeMetadata";
    public const string KodiSeriesImages = "KodiSeriesImages";
    public const string KodiSeasonImages = "KodiSeasonImages";
    public const string KodiEpisodeImages = "KodiEpisodeImages";
}
