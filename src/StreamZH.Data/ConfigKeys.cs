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
}