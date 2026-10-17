// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Security.Cryptography;
using StreamZH.Core;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Host.Logging;

namespace StreamZH.Host.Api;

public static class SettingsEndpoints
{
    public static readonly string[] LogLevels = ["Trace", "Debug", "Information", "Warning", "Error"];

    public static string NewApiKey() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/settings").WithTags("Settings").RequireAuthorization(AuthSchemes.AdminPolicy);

        group.MapGet("/general", (ConfigRepository config, AppPaths paths, LogLevelSwitch logLevel) => new GeneralSettings(
            InstanceName: config.Get(ConfigKeys.InstanceName) ?? "StreamZH",
            LogLevel: logLevel.Minimum.ToString(),
            ApiKey: config.Get(ConfigKeys.ApiKey) ?? string.Empty,
            DataDirectory: paths.DataDirectory,
            LogDirectory: paths.LogDirectory));

        group.MapPut("/general", (UpdateGeneralSettings request, ConfigRepository config, LogLevelSwitch logLevel, ILogger<GeneralSettings> logger) =>
        {
            var errors = new Dictionary<string, string[]>();
            var name = request.InstanceName?.Trim() ?? string.Empty;

            if (name.Length is < 1 or > 50)
            {
                errors["instanceName"] = ["Use 1 to 50 characters."];
            }

            if (!LogLevels.Contains(request.LogLevel) || !Enum.TryParse<LogLevel>(request.LogLevel, out var level))
            {
                errors["logLevel"] = [$"Use one of: {string.Join(", ", LogLevels)}."];
                level = LogLevel.Information;
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            config.Set(ConfigKeys.InstanceName, name);
            config.Set(ConfigKeys.LogLevel, level.ToString());
            logLevel.Minimum = level;
            logger.LogInformation("General settings saved (log level {Level})", level);

            return Results.NoContent();
        });

        group.MapPost("/apikey", (ConfigRepository config, ILogger<GeneralSettings> logger) =>
        {
            var key = NewApiKey();
            config.Set(ConfigKeys.ApiKey, key);
            logger.LogWarning("API key regenerated, connected tools need the new key");
            return new ApiKeyResponse(key);
        }).RequireAuthorization(policy => policy.RequireRole(nameof(UserRole.Owner)));

        return app;
    }
}

public sealed record GeneralSettings(string InstanceName, string LogLevel, string ApiKey, string DataDirectory, string LogDirectory);

public sealed record UpdateGeneralSettings(string? InstanceName, string? LogLevel);

public sealed record ApiKeyResponse(string ApiKey);