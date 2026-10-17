// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using StreamZH.Core;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Host.Backups;
using StreamZH.Host.General;
using StreamZH.Host.Logging;
using StreamZH.Library.Search;

namespace StreamZH.Host.Api;

/// <summary>
/// Settings → General (like Radarr): host, security, API key, proxy, logging, backups; plus restart, shutdown and the
/// update check. One resource for the page; PUT takes only the fields to change (null = keep), so each section of the
/// page saves on its own. Passwords are never sent back: null keeps them, "" clears them.
/// </summary>
public static class GeneralSettingsEndpoints
{
    public static string NewApiKey() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static IEndpointRouteBuilder MapGeneralSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("/api/v1/settings").WithTags("Settings").RequireAuthorization(AuthSchemes.AdminPolicy);

        settings.MapGet("/general", (HttpContext http) => GeneralSettingsResource.From(http.RequestServices));
        settings.MapPut("/general", Update);

        settings.MapPost("/apikey", (ConfigRepository config, ILogger<GeneralSettingsResource> logger) =>
        {
            var key = NewApiKey();
            config.Set(ConfigKeys.ApiKey, key);
            logger.LogWarning("API key regenerated, connected tools need the new key");
            return new ApiKeyResponse(key);
        }).RequireAuthorization(policy => policy.RequireRole(nameof(UserRole.Owner)));

        var system = app.MapGroup("/api/v1/system").WithTags("System").RequireAuthorization(AuthSchemes.AdminPolicy);

        system.MapPost("/restart", (AppLifecycle lifecycle, ClaimsPrincipal user) =>
            lifecycle.Restart(user.Identity?.Name ?? "unknown") ? Results.Accepted() : Results.Conflict());

        system.MapPost("/shutdown", (AppLifecycle lifecycle, ClaimsPrincipal user) =>
            lifecycle.Shutdown(user.Identity?.Name ?? "unknown") ? Results.Accepted() : Results.Conflict());

        system.MapGet("/updates", (UpdateChecker updates) => UpdateInfo.From(updates.Last));
        system.MapPost("/updates/check", async (UpdateChecker updates, CancellationToken ct) => UpdateInfo.From(await updates.CheckAsync(ct)));

        return app;
    }

    private static IResult Update(UpdateGeneralSettings request, HttpContext http, ILogger<GeneralSettingsResource> logger)
    {
        var services = http.RequestServices;
        var host = services.GetRequiredService<HostSettings>();
        var security = services.GetRequiredService<SecuritySettings>();
        var proxy = services.GetRequiredService<ProxySettings>();
        var backups = services.GetRequiredService<BackupSettings>();
        var cache = services.GetRequiredService<SettingsCache>();
        var errors = new Dictionary<string, string[]>();
        void Error(string field, string message) => errors[field] = [message];

        // Host
        var instanceName = request.InstanceName?.Trim();
        if (instanceName is { Length: < 1 or > 50 })
        {
            Error("instanceName", "Use 1 to 50 characters.");
        }

        if (request.BindAddress is { } bind && HostSettings.ValidateBindAddress(bind) is { } bindProblem)
        {
            Error("bindAddress", bindProblem);
        }

        CheckPort(request.Port, "port", Error);
        if (request.UrlBase is { } urlBase && HostSettings.ValidateUrlBase(urlBase) is { } urlBaseProblem)
        {
            Error("urlBase", urlBaseProblem);
        }

        if (HostSettings.ValidateApplicationUrl(request.ApplicationUrl) is { } applicationUrlProblem)
        {
            Error("applicationUrl", applicationUrlProblem);
        }

        CheckPort(request.SslPort, "sslPort", Error);
        var enableSsl = request.EnableSsl ?? host.EnableSsl;
        if (enableSsl && (request.SslPort ?? host.SslPort) == (request.Port ?? host.Port))
        {
            Error("sslPort", "Use another port than the HTTP port.");
        }

        if (enableSsl && (request.EnableSsl is not null || request.SslCertPath is not null || request.SslCertPassword is not null))
        {
            var path = request.SslCertPath?.Trim() ?? host.SslCertPath;
            using var certificate = HostSettings.LoadCertificate(path, request.SslCertPassword ?? host.SslCertPassword, out var certificateProblem);
            if (certificateProblem is not null)
            {
                Error("sslCertPath", certificateProblem);
            }
        }

        // Security
        var method = ParseOrError<AuthenticationMethod>(request.AuthenticationMethod, "authenticationMethod", Error);
        var required = ParseOrError<AuthenticationRequired>(request.AuthenticationRequired, "authenticationRequired", Error);
        var validation = ParseOrError<CertificateValidation>(request.CertificateValidation, "certificateValidation", Error);

        // Proxy
        var proxyType = ParseOrError<ProxyType>(request.ProxyType, "proxyType", Error);
        if ((request.ProxyEnabled ?? proxy.Enabled) && (request.ProxyEnabled is not null || request.ProxyHostname is not null)
            && ProxySettings.ValidateHostname(request.ProxyHostname ?? proxy.Hostname) is { } hostnameProblem)
        {
            Error("proxyHostname", hostnameProblem);
        }

        CheckPort(request.ProxyPort, "proxyPort", Error);
        if (ProxySettings.ValidateFilter(request.ProxyBypassFilter) is { } filterProblem)
        {
            Error("proxyBypassFilter", filterProblem);
        }

        // Logging
        var logLevel = request.LogLevel is null ? null : LoggingSetup.ParseLevel(request.LogLevel);
        if (request.LogLevel is not null && logLevel is null)
        {
            Error("logLevel", $"Use one of: {string.Join(", ", LoggingSetup.Levels)}.");
        }

        var consoleLevel = request.ConsoleLogLevel is null ? null : LoggingSetup.ParseLevel(request.ConsoleLogLevel);
        if (request.ConsoleLogLevel is not null && consoleLevel is null)
        {
            Error("consoleLogLevel", $"Use one of: {string.Join(", ", LoggingSetup.Levels)}.");
        }

        CheckRange(request.LogSizeLimit, 1, LogFileOptions.MaxSizeLimitMb, "logSizeLimit", "MB", Error);
        CheckRange(request.LogFileCount, 1, LogFileOptions.MaxFileCount, "logFileCount", "files", Error);

        // Backups
        if (request.BackupFolder is { } folder && folder.Trim().Length > 0)
        {
            if (!Path.IsPathFullyQualified(folder.Trim()))
            {
                Error("backupFolder", "Enter a full path, e.g. /backups/streamzh or D:\\Backups\\StreamZH.");
            }
            else if (DownloadSettings.CheckWritable(folder.Trim()) is { } folderProblem)
            {
                Error("backupFolder", folderProblem);
            }
        }

        CheckRange(request.BackupInterval, 0, BackupSettings.MaxIntervalDays, "backupInterval", "days", Error);
        CheckRange(request.BackupRetention, 1, BackupSettings.MaxRetentionDays, "backupRetention", "days", Error);

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        // Host (applies at the next start, except the instance name and the application URL)
        SetIfGiven(cache, ConfigKeys.InstanceName, instanceName);
        SetIfGiven(cache, ConfigKeys.BindAddress, request.BindAddress?.Trim());
        SetIfGiven(cache, ConfigKeys.UrlBase, request.UrlBase is null ? null : HostSettings.NormalizeUrlBase(request.UrlBase));
        SetIfGiven(cache, ConfigKeys.ApplicationUrl, request.ApplicationUrl?.Trim().TrimEnd('/'));
        SetIfGiven(cache, ConfigKeys.SslCertPath, request.SslCertPath?.Trim());
        SetIfGiven(cache, ConfigKeys.SslCertPassword, request.SslCertPassword);
        if (request.Port is { } port)
        {
            cache.SetInt(ConfigKeys.Port, port);
        }

        if (request.EnableSsl is { } ssl)
        {
            cache.SetBool(ConfigKeys.EnableSsl, ssl);
        }

        if (request.SslPort is { } sslPort)
        {
            cache.SetInt(ConfigKeys.SslPort, sslPort);
        }

        // Security: the Basic credential cache must not keep a sign-in across a method change.
        security.Update(method, required, validation);
        if (method is not null)
        {
            services.GetRequiredService<BasicCredentialCache>().Clear();
        }

        // Proxy (live)
        if (request.ProxyEnabled is { } proxyEnabled)
        {
            cache.SetBool(ConfigKeys.ProxyEnabled, proxyEnabled);
        }

        if (proxyType is { } type)
        {
            cache.Set(ConfigKeys.ProxyType, type.ToString());
        }

        SetIfGiven(cache, ConfigKeys.ProxyHostname, request.ProxyHostname?.Trim());
        if (request.ProxyPort is { } proxyPort)
        {
            cache.SetInt(ConfigKeys.ProxyPort, proxyPort);
        }

        SetIfGiven(cache, ConfigKeys.ProxyUsername, request.ProxyUsername?.Trim());
        SetIfGiven(cache, ConfigKeys.ProxyPassword, request.ProxyPassword);
        SetIfGiven(cache, ConfigKeys.ProxyBypassFilter, request.ProxyBypassFilter is null ? null : string.Join(",", ProxySettings.ParseFilter(request.ProxyBypassFilter)));
        if (request.ProxyBypassLocalAddresses is { } bypassLocal)
        {
            cache.SetBool(ConfigKeys.ProxyBypassLocalAddresses, bypassLocal);
        }

        // Logging (live)
        if (logLevel is { } fileLevel)
        {
            cache.Set(ConfigKeys.LogLevel, fileLevel.ToString());
            services.GetRequiredService<LogLevelSwitch>().Minimum = fileLevel;
        }

        if (consoleLevel is { } console)
        {
            cache.Set(ConfigKeys.ConsoleLogLevel, console.ToString());
            services.GetRequiredService<ConsoleLogLevelSwitch>().Minimum = console;
        }

        var logFiles = services.GetRequiredService<LogFileOptions>();
        if (request.LogSizeLimit is { } sizeLimit)
        {
            cache.SetInt(ConfigKeys.LogSizeLimitMb, sizeLimit);
            logFiles.SizeLimitMb = sizeLimit;
        }

        if (request.LogFileCount is { } fileCount)
        {
            cache.SetInt(ConfigKeys.LogFileCount, fileCount);
            logFiles.FileCount = fileCount;
        }

        // Backups
        backups.Update(request.BackupFolder, request.BackupInterval, request.BackupRetention);

        var saved = GeneralSettingsResource.From(services);
        logger.LogInformation(
            "General settings saved: authentication {Method} ({Required}), proxy {Proxy}, log level {Level}, backups every {Interval} days{Restart}",
            saved.AuthenticationMethod,
            saved.AuthenticationRequired,
            saved.ProxyEnabled ? $"{saved.ProxyType}://{saved.ProxyHostname}:{saved.ProxyPort}" : "off",
            saved.LogLevel,
            saved.BackupInterval,
            saved.RestartRequired ? ", restart required for the host settings" : string.Empty);
        return Results.Ok(saved);
    }

    /// <summary>"forms", "disabledForLocalAddresses", "socks5": the enum names in camelCase.</summary>
    public static string ApiName<T>(T value)
        where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static T? ParseOrError<T>(string? value, string field, Action<string, string> error)
        where T : struct, Enum
    {
        if (value is null)
        {
            return null;
        }

        if (Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(value, out _))
        {
            return parsed;
        }

        error(field, $"Use one of: {string.Join(", ", Enum.GetValues<T>().Select(ApiName))}.");
        return null;
    }

    private static void CheckPort(int? port, string field, Action<string, string> error)
    {
        if (port is { } p && p is < 1 or > 65535)
        {
            error(field, "Use a port from 1 to 65535.");
        }
    }

    private static void CheckRange(int? value, int min, int max, string field, string unit, Action<string, string> error)
    {
        if (value is { } v && (v < min || v > max))
        {
            error(field, $"Use {min} to {max} {unit}.");
        }
    }

    private static void SetIfGiven(SettingsCache cache, string key, string? value)
    {
        if (value is not null)
        {
            cache.Set(key, value);
        }
    }

    /// <summary>The addresses Kestrel really listens on (empty under the test server).</summary>
    internal static IReadOnlyList<string> Listening(IServiceProvider services, RunningHost running)
    {
        var addresses = services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses;
        return addresses is { Count: > 0 } ? [.. addresses] : running.Addresses;
    }
}

/// <summary>Settings → General. Passwords only as "is set"; <see cref="RestartRequired"/> when host settings changed since the start.</summary>
public sealed record GeneralSettingsResource(
    string InstanceName,
    string BindAddress,
    int Port,
    string UrlBase,
    string ApplicationUrl,
    bool EnableSsl,
    int SslPort,
    string SslCertPath,
    bool SslCertPasswordSet,
    string AuthenticationMethod,
    string? AuthenticationMethodOverride,
    string AuthenticationRequired,
    string CertificateValidation,
    string ApiKey,
    bool ProxyEnabled,
    string ProxyType,
    string ProxyHostname,
    int ProxyPort,
    string ProxyUsername,
    bool ProxyPasswordSet,
    string ProxyBypassFilter,
    bool ProxyBypassLocalAddresses,
    string LogLevel,
    string ConsoleLogLevel,
    int LogSizeLimit,
    int LogFileCount,
    string BackupFolder,
    string DefaultBackupFolder,
    int BackupInterval,
    int BackupRetention,
    string DataDirectory,
    string LogDirectory,
    bool RestartRequired,
    string? HostOverride,
    IReadOnlyList<string> Listening,
    string? HostProblem)
{
    public static GeneralSettingsResource From(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var host = services.GetRequiredService<HostSettings>();
        var security = services.GetRequiredService<SecuritySettings>();
        var proxy = services.GetRequiredService<ProxySettings>();
        var backups = services.GetRequiredService<BackupSettings>();
        var running = services.GetRequiredService<RunningHost>();
        var paths = services.GetRequiredService<AppPaths>();
        var config = services.GetRequiredService<ConfigRepository>();
        var logFiles = services.GetRequiredService<LogFileOptions>();
        var stored = host.Current;
        var restart = running.Override is null ? stored != running.Values : stored.UrlBase != running.Values.UrlBase;

        return new GeneralSettingsResource(
            host.InstanceName,
            host.BindAddress,
            host.Port,
            host.UrlBase,
            host.ApplicationUrl ?? string.Empty,
            host.EnableSsl,
            host.SslPort,
            host.SslCertPath ?? string.Empty,
            host.SslCertPassword is not null,
            GeneralSettingsEndpoints.ApiName(security.StoredMethod),
            security.MethodOverride is { } methodOverride ? GeneralSettingsEndpoints.ApiName(methodOverride) : null,
            GeneralSettingsEndpoints.ApiName(security.Required),
            GeneralSettingsEndpoints.ApiName(security.CertificateValidation),
            config.Get(ConfigKeys.ApiKey) ?? string.Empty,
            proxy.Enabled,
            GeneralSettingsEndpoints.ApiName(proxy.Type),
            proxy.Hostname ?? string.Empty,
            proxy.Port,
            proxy.Username ?? string.Empty,
            proxy.Password is not null,
            string.Join(", ", ProxySettings.ParseFilter(proxy.BypassFilter)),
            proxy.BypassLocalAddresses,
            services.GetRequiredService<LogLevelSwitch>().Minimum.ToString(),
            services.GetRequiredService<ConsoleLogLevelSwitch>().Minimum.ToString(),
            logFiles.SizeLimitMb,
            logFiles.FileCount,
            backups.Folder,
            backups.DefaultFolder,
            backups.IntervalDays,
            backups.RetentionDays,
            paths.DataDirectory,
            paths.LogDirectory,
            restart,
            running.Override,
            GeneralSettingsEndpoints.Listening(services, running),
            running.Problem);
    }
}

/// <summary>Null fields keep their value. Passwords: null keeps, "" clears. Enum fields take the camelCase names.</summary>
public sealed record UpdateGeneralSettings(
    string? InstanceName = null,
    string? BindAddress = null,
    int? Port = null,
    string? UrlBase = null,
    string? ApplicationUrl = null,
    bool? EnableSsl = null,
    int? SslPort = null,
    string? SslCertPath = null,
    string? SslCertPassword = null,
    string? AuthenticationMethod = null,
    string? AuthenticationRequired = null,
    string? CertificateValidation = null,
    bool? ProxyEnabled = null,
    string? ProxyType = null,
    string? ProxyHostname = null,
    int? ProxyPort = null,
    string? ProxyUsername = null,
    string? ProxyPassword = null,
    string? ProxyBypassFilter = null,
    bool? ProxyBypassLocalAddresses = null,
    string? LogLevel = null,
    string? ConsoleLogLevel = null,
    int? LogSizeLimit = null,
    int? LogFileCount = null,
    string? BackupFolder = null,
    int? BackupInterval = null,
    int? BackupRetention = null);

public sealed record ApiKeyResponse(string ApiKey);

/// <summary>Settings → General → Updates: version, how to update this installation, last check.</summary>
public sealed record UpdateInfo(string Version, string Mechanism, string Instructions, UpdateCheckResult LastCheck)
{
    public static UpdateInfo From(UpdateCheckResult last)
    {
        var mode = SystemInfoEndpoints.RuntimeMode();
        var (mechanism, instructions) = mode switch
        {
            "docker" => ("docker", "Pull the new image and recreate the container. Your data in /config stays."),
            "windowsService" => ("manual", "Built-in updates are not available yet. Install the new version over this one; your data stays in the data folder."),
            _ => ("manual", "Built-in updates are not available yet. Replace the program files with the new release and restart; your data stays in the data folder."),
        };
        return new UpdateInfo(AppInfo.Version, mechanism, instructions, last);
    }
}
