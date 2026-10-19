// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using StreamZH.Core;
using StreamZH.Core.Security;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Host.Backups;
using StreamZH.Host.General;
using StreamZH.Host.Logging;
using StreamZH.Host.Security;
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

        settings.MapGet("/general", (HttpContext http) => GeneralSettingsResource.From(http.RequestServices, IsOwner(http.User)));
        settings.MapPut("/general", Update);

        settings.MapPost("/apikey", (ConfigRepository config, ILogger<GeneralSettingsResource> logger) =>
        {
            var key = NewApiKey();
            config.Set(ConfigKeys.ApiKey, key);
            logger.LogWarning("API key regenerated, connected tools need the new key");
            return new ApiKeyResponse(key);
        }).RequireAuthorization(AuthSchemes.OwnerPolicy);

        var system = app.MapGroup("/api/v1/system").WithTags("System").RequireAuthorization(AuthSchemes.AdminPolicy);

        system.MapPost("/restart", (AppLifecycle lifecycle, ClaimsPrincipal user) =>
            lifecycle.Restart(user.Identity?.Name ?? "unknown") ? Results.Accepted() : Results.Conflict());

        system.MapPost("/shutdown", (AppLifecycle lifecycle, ClaimsPrincipal user) =>
            lifecycle.Shutdown(user.Identity?.Name ?? "unknown") ? Results.Accepted() : Results.Conflict());

        system.MapGet("/updates", (UpdateChecker updates) => UpdateInfo.From(updates.Last, updates.InstallKind));
        system.MapPost("/updates/check", async (UpdateChecker updates, CancellationToken ct) => UpdateInfo.From(await updates.CheckAsync(ct), updates.InstallKind));

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
        if (AccessSettings.ValidateHostNames(request.AllowedHostNames) is { } hostNamesProblem)
        {
            Error("allowedHostNames", hostNamesProblem);
        }

        if (AccessSettings.ValidateNetworks(request.TrustedProxies) is { } proxiesProblem)
        {
            Error("trustedProxies", proxiesProblem);
        }

        if (AccessSettings.ValidateNetworks(request.TrustedLocalNetworks) is { } localProblem)
        {
            Error("trustedLocalNetworks", localProblem);
        }

        if (AccessSettings.ValidateClientIpHeader(request.ClientIpHeader) is { } clientIpProblem)
        {
            Error("clientIpHeader", clientIpProblem);
        }

        if (AccessSettings.ValidateHeaderName(request.ExternalUserHeader) is { } headerProblem)
        {
            Error("externalUserHeader", headerProblem);
        }

        // 0.12 (V-12): passwords are typed in, never copied out of the database.
        if (SecretReuse.StoredFormProblem(request.ProxyPassword) is { } proxyStored)
        {
            Error("proxyPassword", proxyStored);
        }

        if (SecretReuse.StoredFormProblem(request.SslCertPassword) is { } sslStored)
        {
            Error("sslCertPassword", sslStored);
        }

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
            else if (services.GetRequiredService<AppPaths>().FolderProblem(folder.Trim(), DataFolderUse.Backups) is { } dataProblem)
            {
                Error("backupFolder", dataProblem);
            }
            else if (DownloadSettings.CheckWritable(folder.Trim()) is { } folderProblem)
            {
                Error("backupFolder", folderProblem);
            }
        }

        CheckRange(request.BackupInterval, 0, BackupSettings.MaxIntervalDays, "backupInterval", "days", Error);
        CheckRange(request.BackupRetention, 1, BackupSettings.MaxRetentionDays, "backupRetention", "days", Error);

        // 0.12: security, proxy and backup settings are the owner's. An Admin (or the API key) may send them unchanged,
        // e.g. when a page saves a whole section, but not change them.
        if (!IsOwner(http.User))
        {
            foreach (var field in OwnerOnlyChanges(request, security, proxy, backups, method, required, validation, proxyType, services.GetRequiredService<AccessSettings>())
                .Concat(HostOwnerOnlyChanges(request, host)))
            {
                Error(field, OwnerOnlyMessage);
            }
        }

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
        services.GetRequiredService<AccessSettings>().Update(request.AllowedHostNames, request.TrustedProxies, request.TrustedLocalNetworks, request.ExternalUserHeader, request.ClientIpHeader);
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

        var saved = GeneralSettingsResource.From(services, IsOwner(http.User));
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

    public const string OwnerOnlyMessage = "Only the owner can change this.";

    /// <summary>The owner: a signed-in Owner, or the owner's own access without account (local network bypass, External).</summary>
    public static bool IsOwner(ClaimsPrincipal user) => user.IsInRole(nameof(UserRole.Owner));

    /// <summary>
    /// The owner-only fields of the request that would change a stored value: authentication (method, required),
    /// certificate validation, the outgoing proxy (it sees the traffic with indexer keys and passwords) and the backups
    /// (folder, interval, retention; a backup holds every secret).
    /// </summary>
    internal static IEnumerable<string> OwnerOnlyChanges(
        UpdateGeneralSettings request,
        SecuritySettings security,
        ProxySettings proxy,
        BackupSettings backups,
        AuthenticationMethod? method,
        AuthenticationRequired? required,
        CertificateValidation? validation,
        ProxyType? proxyType,
        AccessSettings access)
    {
        // 0.12 (FX1): who gets in without signing in and which proxies are believed.
        if (request.AllowedHostNames is { } hosts && string.Join(", ", AccessSettings.ParseHostNames(hosts)) != access.AllowedHostNamesText)
        {
            yield return "allowedHostNames";
        }

        if (request.TrustedProxies is { } proxies && NetworkList.Parse(proxies).Format() != access.TrustedProxiesText)
        {
            yield return "trustedProxies";
        }

        if (request.TrustedLocalNetworks is { } networks && NetworkList.Parse(networks).Format() != access.TrustedLocalNetworksText)
        {
            yield return "trustedLocalNetworks";
        }

        if (request.ClientIpHeader is { } clientIpHeader && !string.Equals(clientIpHeader.Trim(), access.ClientIpHeader, StringComparison.OrdinalIgnoreCase))
        {
            yield return "clientIpHeader";
        }

        if (request.ExternalUserHeader is { } header && header.Trim() != (access.ExternalUserHeader ?? string.Empty))
        {
            yield return "externalUserHeader";
        }

        if (method is { } m && m != security.StoredMethod)
        {
            yield return "authenticationMethod";
        }

        if (required is { } r && r != security.Required)
        {
            yield return "authenticationRequired";
        }

        if (validation is { } v && v != security.CertificateValidation)
        {
            yield return "certificateValidation";
        }

        if (request.ProxyEnabled is { } enabled && enabled != proxy.Enabled)
        {
            yield return "proxyEnabled";
        }

        if (proxyType is { } type && type != proxy.Type)
        {
            yield return "proxyType";
        }

        if (request.ProxyHostname is { } hostname && hostname.Trim() != (proxy.Hostname ?? string.Empty))
        {
            yield return "proxyHostname";
        }

        if (request.ProxyPort is { } proxyPort && proxyPort != proxy.Port)
        {
            yield return "proxyPort";
        }

        if (request.ProxyUsername is { } username && username.Trim() != (proxy.Username ?? string.Empty))
        {
            yield return "proxyUsername";
        }

        if (request.ProxyPassword is { } password && (password.Length > 0 || proxy.Password is not null))
        {
            yield return "proxyPassword";
        }

        if (request.ProxyBypassFilter is { } filter
            && !ProxySettings.ParseFilter(filter).SequenceEqual(ProxySettings.ParseFilter(proxy.BypassFilter), StringComparer.Ordinal))
        {
            yield return "proxyBypassFilter";
        }

        if (request.ProxyBypassLocalAddresses is { } bypass && bypass != proxy.BypassLocalAddresses)
        {
            yield return "proxyBypassLocalAddresses";
        }

        if (request.BackupFolder is { } folder
            && (folder.Trim().Length == 0 ? !backups.IsDefaultFolder : !SamePath(folder.Trim(), backups.Folder)))
        {
            yield return "backupFolder";
        }

        if (request.BackupInterval is { } interval && interval != backups.IntervalDays)
        {
            yield return "backupInterval";
        }

        if (request.BackupRetention is { } retention && retention != backups.RetentionDays)
        {
            yield return "backupRetention";
        }
    }

    /// <summary>
    /// 0.12 (V-04, V-11): where and how StreamZH listens and which address it trusts as its own are the owner's too. The
    /// application URL decides which Host and Origin count as StreamZH's own (DNS rebinding, cross-site checks); bind
    /// address and port decide who can reach it; the certificate and URL base what the browser talks to. Only the instance
    /// name stays open to Admins.
    /// </summary>
    internal static IEnumerable<string> HostOwnerOnlyChanges(UpdateGeneralSettings request, HostSettings host)
    {
        if (request.ApplicationUrl is { } applicationUrl
            && !string.Equals(applicationUrl.Trim().TrimEnd('/'), host.ApplicationUrl ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            yield return "applicationUrl";
        }

        if (request.BindAddress is { } bind && !string.Equals(bind.Trim(), host.BindAddress, StringComparison.OrdinalIgnoreCase))
        {
            yield return "bindAddress";
        }

        if (request.Port is { } port && port != host.Port)
        {
            yield return "port";
        }

        if (request.UrlBase is { } urlBase && HostSettings.ValidateUrlBase(urlBase) is null && HostSettings.NormalizeUrlBase(urlBase) != host.UrlBase)
        {
            yield return "urlBase";
        }

        if (request.EnableSsl is { } ssl && ssl != host.EnableSsl)
        {
            yield return "enableSsl";
        }

        if (request.SslPort is { } sslPort && sslPort != host.SslPort)
        {
            yield return "sslPort";
        }

        if (request.SslCertPath is { } certPath && !string.Equals(certPath.Trim(), host.SslCertPath ?? string.Empty, StringComparison.Ordinal))
        {
            yield return "sslCertPath";
        }

        if (request.SslCertPassword is { } certPassword && (certPassword.Length > 0 || host.SslCertPassword is not null))
        {
            yield return "sslCertPassword";
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(a),
            Path.TrimEndingDirectorySeparator(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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
    string? HostProblem,
    string AllowedHostNames = "",
    string TrustedProxies = "",
    string TrustedLocalNetworks = "",
    string ExternalUserHeader = "",
    bool RunningInContainer = false,
    string ClientIpHeader = AccessSettings.ClientIpHeaderAuto)
{
    /// <summary>Shown instead of the API key to everyone but the owner (0.12).</summary>
    public const string MaskedApiKey = "********";

    /// <param name="owner">Only the owner sees the API key; Admins get <see cref="MaskedApiKey"/>.</param>
    public static GeneralSettingsResource From(IServiceProvider services, bool owner)
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
        var access = services.GetRequiredService<AccessSettings>();

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
            owner ? config.Get(ConfigKeys.ApiKey) ?? string.Empty : MaskedApiKey,
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
            running.Problem,
            access.AllowedHostNamesText,
            access.TrustedProxiesText,
            access.TrustedLocalNetworksText,
            access.ExternalUserHeader ?? string.Empty,
            ContainerDetection.IsContainer,
            access.ClientIpHeader);
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
    int? BackupRetention = null,
    string? AllowedHostNames = null,
    string? TrustedProxies = null,
    string? TrustedLocalNetworks = null,
    string? ExternalUserHeader = null,
    string? ClientIpHeader = null);

public sealed record ApiKeyResponse(string ApiKey);

/// <summary>Settings → General → Updates: version, how to update this installation, last check.</summary>
/// <param name="Package">How this copy was installed: docker, windows-installer, deb, rpm or archive (<see cref="ReleaseAssets"/>).</param>
public sealed record UpdateInfo(string Version, string Mechanism, string Package, string Instructions, UpdateCheckResult LastCheck)
{
    public static UpdateInfo From(UpdateCheckResult last, string package)
    {
        var mode = SystemInfoEndpoints.RuntimeMode();
        var (mechanism, instructions) = (mode, package) switch
        {
            ("docker", _) => ("docker", "Pull the new image and recreate the container. Your data in /config stays."),
            (_, ReleaseAssets.WindowsInstaller) => ("manual", "Built-in updates are not available. Download the new setup and run it: it stops StreamZH, updates the program in place and starts it again. Your data and settings stay."),
            (_, ReleaseAssets.Deb) => ("manual", "Built-in updates are not available. Install the new .deb with sudo apt install ./<file>.deb: it restarts the service and keeps your data in /var/lib/streamzh."),
            (_, ReleaseAssets.Rpm) => ("manual", "Built-in updates are not available. Install the new .rpm with sudo dnf install ./<file>.rpm: it restarts the service and keeps your data in /var/lib/streamzh."),
            ("windowsService", _) => ("manual", "Built-in updates are not available. Stop the StreamZH service, replace the program files with the new release and start the service again; your data stays in the data folder."),
            ("systemd", _) => ("manual", "Built-in updates are not available. Run install-linux.sh of the new release; it stops the service, replaces the program files and starts it again. Your data stays in the data folder."),
            _ => ("manual", "Built-in updates are not available. Stop StreamZH, replace the program files with the new release and start it again; your data stays in the data folder."),
        };
        return new UpdateInfo(AppInfo.Version, mechanism, package, instructions, last);
    }
}
