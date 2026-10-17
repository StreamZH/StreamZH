// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.Extensions.Logging.Console;
using StreamZH.Core;
using StreamZH.Data;

namespace StreamZH.Host.Logging;

/// <summary>
/// The log file and the console, with the levels and the rotation of Settings → General → Logging. All of them change
/// at runtime through the switches registered here.
/// </summary>
public static class LoggingSetup
{
    public static readonly string[] Levels = ["Trace", "Debug", "Information", "Warning", "Error"];

    public static FileLoggerProvider AddStreamZhLogging(this WebApplicationBuilder builder, AppPaths paths, ConfigRepository config)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(config);

        var fileLevel = new LogLevelSwitch(ParseLevel(config.Get(ConfigKeys.LogLevel)) ?? LogLevel.Information);
        var consoleLevel = new ConsoleLogLevelSwitch(ParseLevel(config.Get(ConfigKeys.ConsoleLogLevel)) ?? DefaultConsoleLevel(builder.Environment));
        var options = new LogFileOptions
        {
            SizeLimitMb = Clamp(config.Get(ConfigKeys.LogSizeLimitMb), LogFileOptions.DefaultSizeLimitMb, 1, LogFileOptions.MaxSizeLimitMb),
            FileCount = Clamp(config.Get(ConfigKeys.LogFileCount), LogFileOptions.DefaultFileCount, 1, LogFileOptions.MaxFileCount),
        };
        var fileLogger = new FileLoggerProvider(paths.LogDirectory, fileLevel, options);
        builder.Logging.AddProvider(fileLogger);

        // The log file follows the level from the settings page; framework noise stays at Warning.
        builder.Logging.AddFilter<FileLoggerProvider>(null, LogLevel.Trace);
        builder.Logging.AddFilter<FileLoggerProvider>("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter<FileLoggerProvider>("Microsoft.Hosting.Lifetime", LogLevel.Information);

        // The console too (default Information, like before); the filter is asked on every message, so changes apply at once.
        builder.Logging.AddFilter<ConsoleLoggerProvider>(null, level => level >= consoleLevel.Minimum);
        builder.Logging.AddFilter<ConsoleLoggerProvider>("Microsoft", level => level >= Max(LogLevel.Warning, consoleLevel.Minimum));
        builder.Logging.AddFilter<ConsoleLoggerProvider>("Microsoft.Hosting.Lifetime", level => level >= consoleLevel.Minimum);

        // Keys are stored unencrypted in the data folder by design (same as Sonarr/Radarr); no warning on every start.
        builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error);
        builder.Logging.AddFilter<FileLoggerProvider>("Microsoft.AspNetCore.DataProtection", LogLevel.Error);
        builder.Logging.AddFilter<ConsoleLoggerProvider>("Microsoft.AspNetCore.DataProtection", LogLevel.Error);

        // One line per outgoing HTTP request (TMDB, indexers) is noise; keep warnings and errors only.
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
        builder.Logging.AddFilter<FileLoggerProvider>("System.Net.Http.HttpClient", LogLevel.Warning);
        builder.Logging.AddFilter<ConsoleLoggerProvider>("System.Net.Http.HttpClient", level => level >= Max(LogLevel.Warning, consoleLevel.Minimum));

        builder.Services.AddSingleton(fileLevel);
        builder.Services.AddSingleton(consoleLevel);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(fileLogger);
        return fileLogger;
    }

    /// <summary>Information, Debug while developing (as appsettings.Development.json had it).</summary>
    public static LogLevel DefaultConsoleLevel(IHostEnvironment environment) =>
        environment.IsDevelopment() ? LogLevel.Debug : LogLevel.Information;

    public static LogLevel? ParseLevel(string? value) =>
        Levels.Contains(value) && Enum.TryParse<LogLevel>(value, out var level) ? level : null;

    private static int Clamp(string? value, int fallback, int min, int max) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? Math.Clamp(parsed, min, max) : fallback;

    private static LogLevel Max(LogLevel a, LogLevel b) => a > b ? a : b;
}
