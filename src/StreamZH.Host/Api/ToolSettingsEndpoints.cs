// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Library.PostProcessing;

namespace StreamZH.Host.Api;

/// <summary>Settings → Downloads → Tools: where par2 and 7-Zip are, and whether they were found.</summary>
public static class ToolSettingsEndpoints
{
    public static IEndpointRouteBuilder MapToolSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/settings/tools").WithTags("Settings").RequireAuthorization(AuthSchemes.AdminPolicy);

        group.MapGet("/", (PostProcessTools tools, CancellationToken ct) => ToolSettingsResource.FromAsync(tools, ct));

        group.MapPut("/", async (UpdateToolSettings request, ConfigRepository config, PostProcessTools tools, ILogger<ToolSettingsResource> logger, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            var par2 = request.Par2Path?.Trim();
            var sevenZip = request.SevenZipPath?.Trim();

            if (!string.IsNullOrEmpty(par2) && tools.ResolveConfigured(PostProcessTool.Par2, par2) is null)
            {
                errors["par2Path"] = ["par2 was not found there. Enter the path of par2 (par2.exe on Windows) or its folder, or leave it empty to search automatically."];
            }

            if (!string.IsNullOrEmpty(sevenZip) && tools.ResolveConfigured(PostProcessTool.SevenZip, sevenZip) is null)
            {
                errors["sevenZipPath"] = ["7-Zip was not found there. Enter the path of 7z (7z.exe on Windows) or its folder, or leave it empty to search automatically."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            // null = unchanged, "" = find automatically.
            if (par2 is not null)
            {
                config.Set(ConfigKeys.Par2Path, par2);
            }

            if (sevenZip is not null)
            {
                config.Set(ConfigKeys.SevenZipPath, sevenZip);
            }

            var resource = await ToolSettingsResource.FromAsync(tools, ct);
            logger.LogInformation("Tool settings saved: par2 {Par2}, 7-Zip {SevenZip}", resource.Par2ResolvedPath ?? "not found", resource.SevenZipResolvedPath ?? "not found");
            return Results.Ok(resource);
        });

        return app;
    }
}

/// <param name="Par2Path">Configured path; "" = search automatically.</param>
/// <param name="Par2ResolvedPath">The executable that is used, wherever it was found.</param>
/// <param name="Par2Source">Configured, Bundled, Path, WindowsDefault or NotFound.</param>
/// <param name="Os">Windows, Linux or macOS, for the install hints.</param>
public sealed record ToolSettingsResource(
    string Par2Path,
    string SevenZipPath,
    bool Par2Found,
    bool SevenZipFound,
    string? Par2Version,
    string? SevenZipVersion,
    string? Par2ResolvedPath,
    string? SevenZipResolvedPath,
    string Par2Source,
    string SevenZipSource,
    string? Par2Problem,
    string? SevenZipProblem,
    string BundledFolder,
    string Os)
{
    public static async Task<ToolSettingsResource> FromAsync(PostProcessTools tools, CancellationToken cancellationToken)
    {
        var par2 = tools.Find(PostProcessTool.Par2);
        var sevenZip = tools.Find(PostProcessTool.SevenZip);
        var par2Version = par2.Path is null ? null : await tools.GetVersionAsync(par2.Path, cancellationToken);
        var sevenZipVersion = sevenZip.Path is null ? null : await tools.GetVersionAsync(sevenZip.Path, cancellationToken);

        return new ToolSettingsResource(
            par2.ConfiguredPath,
            sevenZip.ConfiguredPath,
            par2.Found,
            sevenZip.Found,
            par2Version,
            sevenZipVersion,
            par2.Path,
            sevenZip.Path,
            par2.Source.ToString(),
            sevenZip.Source.ToString(),
            par2.Problem,
            sevenZip.Problem,
            tools.Environment.BundledDirectory,
            tools.Environment.IsWindows ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux");
    }
}

/// <summary>null leaves a path unchanged, "" switches back to the automatic search.</summary>
public sealed record UpdateToolSettings(string? Par2Path, string? SevenZipPath);
