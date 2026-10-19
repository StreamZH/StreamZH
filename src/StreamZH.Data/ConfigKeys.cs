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
    // General, UI and backups (0.6); empty = default. Host keys apply at the next start.
    public const string BindAddress = "BindAddress";
    public const string Port = "Port";
    public const string UrlBase = "UrlBase";
    public const string ApplicationUrl = "ApplicationUrl";
    public const string EnableSsl = "EnableSsl";
    public const string SslPort = "SslPort";
    public const string SslCertPath = "SslCertPath";
    public const string SslCertPassword = "SslCertPassword";
    public const string AuthenticationMethod = "AuthenticationMethod";
    public const string AuthenticationRequired = "AuthenticationRequired";
    public const string CertificateValidation = "CertificateValidation";
    public const string ProxyEnabled = "ProxyEnabled";
    public const string ProxyType = "ProxyType";
    public const string ProxyHostname = "ProxyHostname";
    public const string ProxyPort = "ProxyPort";
    public const string ProxyUsername = "ProxyUsername";
    public const string ProxyPassword = "ProxyPassword";
    public const string ProxyBypassFilter = "ProxyBypassFilter";
    public const string ProxyBypassLocalAddresses = "ProxyBypassLocalAddresses";
    public const string LogSizeLimitMb = "LogSizeLimitMb";
    public const string LogFileCount = "LogFileCount";
    public const string ConsoleLogLevel = "ConsoleLogLevel";
    public const string BackupFolder = "BackupFolder";
    public const string BackupIntervalDays = "BackupIntervalDays";
    public const string BackupRetentionDays = "BackupRetentionDays";
    public const string UiShortDateFormat = "UiShortDateFormat";
    public const string UiLongDateFormat = "UiLongDateFormat";
    public const string UiTimeFormat = "UiTimeFormat";
    public const string UiShowRelativeDates = "UiShowRelativeDates";
    // Calendar (0.7)
    public const string UiFirstDayOfWeek = "UiFirstDayOfWeek";

    // Import lists (0.7)
    public const string ImportListSyncInterval = "ImportListSyncInterval";
    public const string ImportListCleanLibraryLevel = "ImportListCleanLibraryLevel";

    // TRaSH (0.8): settings, then the state of the local copy of the guides.
    public const string TrashEnabled = "TrashEnabled";
    public const string TrashRepository = "TrashRepository";
    public const string TrashBranch = "TrashBranch";
    public const string TrashSyncIntervalHours = "TrashSyncIntervalHours";
    public const string TrashAutoUpdate = "TrashAutoUpdate";
    public const string TrashKeepScoreOverrides = "TrashKeepScoreOverrides";
    public const string TrashFolder = "TrashFolder";
    public const string TrashCommit = "TrashCommit";
    public const string TrashETag = "TrashETag";
    public const string TrashSource = "TrashSource";
    public const string TrashLastSync = "TrashLastSync";
    public const string TrashLastError = "TrashLastError";
    public const string TrashQualitySizes = "TrashQualitySizes";

    // Media info (0.8.1): ffprobe path, analysing files, the weekly refresh.
    public const string FfprobePath = "FfprobePath";
    public const string MediaInfoAnalyze = "MediaInfoAnalyze";
    public const string MediaInfoRefreshIntervalDays = "MediaInfoRefreshIntervalDays";

    // Alternative titles (0.8.1)
    public const string IndexerSearchAlternativeTitles = "IndexerSearchAlternativeTitles";

    // Migration (0.9): side-by-side mode — StreamZH grabs nothing automatically while Sonarr/Radarr still run.
    public const string AutomationHold = "AutomationHold";
    public const string AutomationHoldSince = "AutomationHoldSince";

    // Requests (0.10): Settings → Requests.
    public const string RequestsEnabled = "RequestsEnabled";
    public const string RequestsMovieProfileId = "RequestsMovieProfileId";
    public const string RequestsSeriesProfileId = "RequestsSeriesProfileId";
    public const string RequestsMovieRootFolderId = "RequestsMovieRootFolderId";
    public const string RequestsSeriesRootFolderId = "RequestsSeriesRootFolderId";
    public const string RequestsAutoApproveFriends = "RequestsAutoApproveFriends";
    public const string RequestsAutoApproveRequesters = "RequestsAutoApproveRequesters";
    public const string RequestsMovieLimit = "RequestsMovieLimit";
    public const string RequestsSeriesLimit = "RequestsSeriesLimit";
    public const string RequestsLimitDays = "RequestsLimitDays";
    public const string RequestsSearchOnApprove = "RequestsSearchOnApprove";
    public const string RequestsSeriesMonitor = "RequestsSeriesMonitor";
    public const string RequestsNotifyOnAvailable = "RequestsNotifyOnAvailable";

    // Discord bot (0.10): Settings → Discord; the token is a secret (masked in the API, never logged).
    public const string DiscordEnabled = "DiscordEnabled";
    public const string DiscordBotToken = "DiscordBotToken";
    public const string DiscordApplicationId = "DiscordApplicationId";
    public const string DiscordGuildIds = "DiscordGuildIds";
    public const string DiscordChannelIds = "DiscordChannelIds";
    public const string DiscordAllowGuests = "DiscordAllowGuests";
    public const string DiscordGuestRoleIds = "DiscordGuestRoleIds";
    public const string DiscordNotifyChannelId = "DiscordNotifyChannelId";
    public const string DiscordDmRequesters = "DiscordDmRequesters";
    public const string DiscordAllowDirectMessages = "DiscordAllowDirectMessages";
    public const string DiscordMovieCommand = "DiscordMovieCommand";
    public const string DiscordTvCommand = "DiscordTvCommand";
    public const string DiscordSeriesSeasons = "DiscordSeriesSeasons";
    public const string DiscordStatusText = "DiscordStatusText";

    // Torrents (0.11, agent I): protocol preference and delays (Settings → Indexers); pending releases waiting for their delay (JSON).
    public const string IndexerPreferredProtocol = "IndexerPreferredProtocol";
    public const string IndexerUsenetDelay = "IndexerUsenetDelay";
    public const string IndexerTorrentDelay = "IndexerTorrentDelay";
    public const string IndexerBypassDelayIfHighestQuality = "IndexerBypassDelayIfHighestQuality";
    public const string PendingReleases = "PendingReleases";
    // Torrent engine (0.11): StreamZH's own torrent client (Settings → Download clients → StreamZH).
    public const string TorrentEngineListenPort = "TorrentEngineListenPort";
    public const string TorrentEngineBindAddress = "TorrentEngineBindAddress";
    public const string TorrentEngineMaxConnections = "TorrentEngineMaxConnections";
    public const string TorrentEngineMaxConnectionsPerTorrent = "TorrentEngineMaxConnectionsPerTorrent";
    public const string TorrentEngineDownloadLimitKBps = "TorrentEngineDownloadLimitKBps";
    public const string TorrentEngineUploadLimitKBps = "TorrentEngineUploadLimitKBps";
    public const string TorrentEngineDht = "TorrentEngineDht";
    public const string TorrentEnginePex = "TorrentEnginePex";
    public const string TorrentEngineLocalPeerDiscovery = "TorrentEngineLocalPeerDiscovery";
    public const string TorrentEngineEncryption = "TorrentEngineEncryption";
    public const string TorrentEnginePortForwarding = "TorrentEnginePortForwarding";
    public const string TorrentEngineSeedRatio = "TorrentEngineSeedRatio";
    public const string TorrentEngineSeedTimeMinutes = "TorrentEngineSeedTimeMinutes";

    // Security (0.12, FX1): Settings → General → Security (advanced) and the read-only iCal feed token.
    public const string AllowedHostNames = "AllowedHostNames";
    public const string TrustedProxies = "TrustedProxies";
    public const string TrustedLocalNetworks = "TrustedLocalNetworks";
    public const string ExternalUserHeader = "ExternalUserHeader";
    public const string CalendarFeedToken = "CalendarFeedToken";
}
