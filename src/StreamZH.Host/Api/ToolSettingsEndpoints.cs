// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Library.PostProcessing;

namespace StreamZH.Host.Api;

/// <summary>Settings → Downloads → Tools: where par2, 7-Zip and ffprobe are, whether they were found, and installing them.</summary>
public static class ToolSettingsEndpoints
{
    /// <summary>Why a configured path is not used: missing, or (0.12) a file that is not named like the tool.</summary>
    internal static string NotFoundMessage(PostProcessTool tool, string configured)
    {
        var name = PostProcessTools.DisplayName(tool);
        return File.Exists(configured)
            ? $"This file is not {name}. For safety StreamZH only runs a file named {PostProcessTools.FileNameHint(tool)}."
            : $"{name} was not found there. Enter the path of {PostProcessTools.FileNameHint(tool)} or its folder, or leave it empty to search automatically.";
    }

    public static IEndpointRouteBuilder MapToolSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/settings/tools").WithTags("Settings").RequireAuthorization(AuthSchemes.AdminPolicy);

        group.MapGet("/", (PostProcessTools tools, ToolInstaller installer, IPostProcessToolSettings settings, CancellationToken ct) =>
            ToolSettingsResource.FromAsync(tools, installer, settings, ct));

        group.MapPut("/", async (
            UpdateToolSettings request,
            ConfigRepository config,
            PostProcessTools tools,
            ToolInstaller installer,
            IPostProcessToolSettings settings,
            IEventBus events,
            ILogger<ToolSettingsResource> logger,
            CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            var par2 = request.Par2Path?.Trim();
            var sevenZip = request.SevenZipPath?.Trim();
            var ffprobe = request.FfprobePath?.Trim();

            foreach (var (tool, field, value) in new[] { (PostProcessTool.Par2, "par2Path", par2), (PostProcessTool.SevenZip, "sevenZipPath", sevenZip), (PostProcessTool.FFprobe, "ffprobePath", ffprobe) })
            {
                if (!string.IsNullOrEmpty(value) && tools.ResolveConfigured(tool, value) is null)
                {
                    errors[field] = [NotFoundMessage(tool, value)];
                }
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

            if (ffprobe is not null)
            {
                config.Set(ConfigKeys.FfprobePath, ffprobe);
            }

            if (request.AutoInstall is { } autoInstall)
            {
                config.Set(ConfigKeys.ToolsAutoInstall, autoInstall ? "true" : "false");
            }

            // A tool that is left to the automatic search and not found anywhere is installed in the background.
            foreach (var tool in ToolInstaller.AllTools)
            {
                if (tools.Find(tool).ConfiguredPath.Length == 0 && installer.StartIfMissing(tool))
                {
                    logger.LogInformation("{Tool} not found, installing it", PostProcessTools.DisplayName(tool));
                }
            }

            // A notice about a tool that could not be installed disappears once the tool is found.
            await events.PublishAsync(new OngoingChanged(), ct);

            var resource = await ToolSettingsResource.FromAsync(tools, installer, settings, ct);
            logger.LogInformation(
                "Tool settings saved: par2 {Par2}, 7-Zip {SevenZip}, ffprobe {Ffprobe}, install automatically {AutoInstall}",
                resource.Par2ResolvedPath ?? "not found",
                resource.SevenZipResolvedPath ?? "not found",
                resource.FfprobeResolvedPath ?? "not found",
                resource.AutoInstall);
            return Results.Ok(resource);
        }).RequireAuthorization(AuthSchemes.OwnerPolicy); // 0.12: StreamZH runs what is configured here.

        // Starts the installation (or joins the one already running) and answers when it has finished, a few seconds later.
        group.MapPost("/{tool}/install", async (
            string tool,
            PostProcessTools tools,
            ToolInstaller installer,
            IPostProcessToolSettings settings,
            CancellationToken ct) =>
        {
            PostProcessTool? which = tool.ToLowerInvariant() switch
            {
                "par2" => PostProcessTool.Par2,
                "sevenzip" or "7zip" or "7-zip" => PostProcessTool.SevenZip,
                "ffprobe" or "ffmpeg" => PostProcessTool.FFprobe,
                _ => null,
            };
            if (which is not { } selected)
            {
                return Results.Problem($"Unknown tool \"{tool}\". Use par2, sevenZip or ffprobe.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (!installer.IsInstallable(selected))
            {
                return Results.Problem(installer.NotInstallableMessage(selected), statusCode: StatusCodes.Status400BadRequest);
            }

            var result = await installer.InstallAsync(selected, ct);
            if (!result.Success)
            {
                return Results.Problem(
                    $"{PostProcessTools.DisplayName(selected)} could not be installed: {result.Error}",
                    title: "Installation failed",
                    statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Ok(await ToolSettingsResource.FromAsync(tools, installer, settings, ct));
        });

        return app;
    }
}

/// <param name="Par2Path">Configured path; "" = search automatically.</param>
/// <param name="Par2ResolvedPath">The executable that is used, wherever it was found.</param>
/// <param name="Par2Source">Configured, Installed, Path, WindowsDefault or NotFound.</param>
/// <param name="Os">Windows, Linux or macOS, for the install hints.</param>
/// <param name="Par2Installable">StreamZH can install par2 on this system.</param>
/// <param name="Par2Installing">An installation is running right now.</param>
/// <param name="Par2InstallProgress">0..1 while installing, otherwise null.</param>
/// <param name="Par2InstallError">Why the last installation failed, null when it did not.</param>
/// <param name="Par2InstalledVersion">Version of the copy installed by StreamZH (from its version.json), null when there is none.</param>
/// <param name="Par2InstallVersion">The version StreamZH installs; null when it cannot install it here.</param>
/// <param name="AutoInstall">Missing tools are installed automatically.</param>
/// <param name="InstallFolder">&lt;data&gt;/tools, where StreamZH installs the tools.</param>
/// <param name="FfprobePath">ffprobe (media info), with the same fields as par2 and 7-Zip.</param>
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
    string Os,
    bool Par2Installable,
    bool SevenZipInstallable,
    bool Par2Installing,
    bool SevenZipInstalling,
    double? Par2InstallProgress,
    double? SevenZipInstallProgress,
    string? Par2InstallError,
    string? SevenZipInstallError,
    string? Par2InstalledVersion,
    string? SevenZipInstalledVersion,
    string? Par2InstallVersion,
    string? SevenZipInstallVersion,
    bool AutoInstall,
    string? InstallFolder,
    string FfprobePath,
    bool FfprobeFound,
    string? FfprobeVersion,
    string? FfprobeResolvedPath,
    string FfprobeSource,
    string? FfprobeProblem,
    bool FfprobeInstallable,
    bool FfprobeInstalling,
    double? FfprobeInstallProgress,
    string? FfprobeInstallError,
    string? FfprobeInstalledVersion,
    string? FfprobeInstallVersion)
{
    public static async Task<ToolSettingsResource> FromAsync(
        PostProcessTools tools, ToolInstaller installer, IPostProcessToolSettings settings, CancellationToken cancellationToken)
    {
        var par2 = tools.Find(PostProcessTool.Par2);
        var sevenZip = tools.Find(PostProcessTool.SevenZip);
        var par2Version = par2.Path is null ? null : await tools.GetVersionAsync(par2.Path, cancellationToken);
        var sevenZipVersion = sevenZip.Path is null ? null : await tools.GetVersionAsync(sevenZip.Path, cancellationToken);
        var par2Status = installer.Status(PostProcessTool.Par2);
        var sevenZipStatus = installer.Status(PostProcessTool.SevenZip);
        var ffprobe = tools.Find(PostProcessTool.FFprobe);
        var ffprobeVersion = ffprobe.Path is null ? null : await tools.GetVersionAsync(ffprobe.Path, cancellationToken);
        var ffprobeStatus = installer.Status(PostProcessTool.FFprobe);

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
            tools.Environment.IsWindows ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux",
            installer.IsInstallable(PostProcessTool.Par2),
            installer.IsInstallable(PostProcessTool.SevenZip),
            par2Status.Installing,
            sevenZipStatus.Installing,
            par2Status.Installing ? par2Status.Progress : null,
            sevenZipStatus.Installing ? sevenZipStatus.Progress : null,
            par2Status.Error,
            sevenZipStatus.Error,
            installer.Installed(PostProcessTool.Par2)?.Version,
            installer.Installed(PostProcessTool.SevenZip)?.Version,
            installer.Package(PostProcessTool.Par2)?.Version,
            installer.Package(PostProcessTool.SevenZip)?.Version,
            settings.AutoInstall,
            tools.Environment.InstalledDirectory,
            ffprobe.ConfiguredPath,
            ffprobe.Found,
            ffprobeVersion,
            ffprobe.Path,
            ffprobe.Source.ToString(),
            ffprobe.Problem,
            installer.IsInstallable(PostProcessTool.FFprobe),
            ffprobeStatus.Installing,
            ffprobeStatus.Installing ? ffprobeStatus.Progress : null,
            ffprobeStatus.Error,
            installer.Installed(PostProcessTool.FFprobe)?.Version,
            installer.Package(PostProcessTool.FFprobe)?.Version);
    }
}

/// <summary>null leaves a value unchanged; "" switches a path back to the automatic search.</summary>
public sealed record UpdateToolSettings(string? Par2Path, string? SevenZipPath, bool? AutoInstall = null, string? FfprobePath = null);
