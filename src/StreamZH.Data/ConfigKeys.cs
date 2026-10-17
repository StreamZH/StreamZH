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