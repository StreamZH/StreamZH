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
}
