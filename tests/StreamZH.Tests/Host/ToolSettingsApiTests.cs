// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Downloads;
using StreamZH.Host.Api;
using StreamZH.Library.PostProcessing;
using StreamZH.Tests.PostProcessing;

namespace StreamZH.Tests.Host;

public sealed class ToolSettingsApiTests : IDisposable
{
    private readonly StreamZhFactory _factory = new();

    [Fact]
    public async Task Reads_and_updates_tool_paths()
    {
        var client = await _factory.CreateOwnerClientAsync();

        var settings = await client.GetFromJsonAsync<ToolSettingsResource>("/api/v1/settings/tools");
        Assert.Equal(string.Empty, settings!.Par2Path);
        Assert.Equal(string.Empty, settings.SevenZipPath);
        Assert.Equal(settings.Par2ResolvedPath is not null, settings.Par2Found);
        Assert.Contains(settings.Os, new[] { "Windows", "Linux", "macOS" });
        Assert.EndsWith("tools", settings.BundledFolder);

        var invalid = await client.PutAsJsonAsync("/api/v1/settings/tools", new { par2Path = "/does/not/exist/par2", sevenZipPath = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("par2Path", await invalid.Content.ReadAsStringAsync());

        // A folder that contains the tool is accepted; the response shows what is used now.
        var folder = Path.Combine(_factory.DataDirectory, "my-tools");
        Directory.CreateDirectory(folder);
        var fake7z = Path.Combine(folder, OperatingSystem.IsWindows() ? "7z.exe" : "7z");
        await File.WriteAllTextAsync(fake7z, "not really 7-Zip");

        var saved = await client.PutAsJsonAsync("/api/v1/settings/tools", new { sevenZipPath = folder });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var updated = await saved.Content.ReadFromJsonAsync<ToolSettingsResource>();
        Assert.Equal(folder, updated!.SevenZipPath);
        Assert.True(updated.SevenZipFound);
        Assert.Equal(fake7z, updated.SevenZipResolvedPath);
        Assert.Equal("Configured", updated.SevenZipSource);
        Assert.Null(updated.SevenZipVersion);
        Assert.Equal(settings.Par2Path, updated.Par2Path);

        // "" goes back to the automatic search.
        var reset = await client.PutAsJsonAsync("/api/v1/settings/tools", new { sevenZipPath = "" });
        var automatic = await reset.Content.ReadFromJsonAsync<ToolSettingsResource>();
        Assert.Equal(string.Empty, automatic!.SevenZipPath);
        Assert.NotEqual("Configured", automatic.SevenZipSource);
    }

    [Fact]
    public async Task Install_endpoint_installs_into_the_data_folder()
    {
        var (par2, data) = TestToolPackages.Par2();
        var sevenZip = par2 with
        {
            Tool = PostProcessTool.SevenZip,
            Name = "7-Zip",
            Version = "26.03",
            Download = TestToolPackages.Download("7z.zip", [1, 2, 3]),
            Files = [OperatingSystem.IsWindows() ? "7z.exe" : "7zz"],
        };
        _factory.ToolDownloads.Add(par2.Download, data);
        _factory.ToolCatalog = new ToolCatalog((tool, _) => tool == PostProcessTool.Par2 ? par2 : sevenZip);
        var client = await _factory.CreateOwnerClientAsync();
        var toolsFolder = Path.Combine(_factory.DataDirectory, "tools");

        var before = (await client.GetFromJsonAsync<ToolSettingsResource>("/api/v1/settings/tools"))!;
        Assert.True(before.Par2Installable);
        Assert.True(before.SevenZipInstallable);
        Assert.False(before.Par2Installing);
        Assert.Null(before.Par2InstallProgress);
        Assert.Null(before.Par2InstallError);
        Assert.Null(before.Par2InstalledVersion);
        Assert.Equal("1.5.0", before.Par2InstallVersion);
        Assert.Equal("26.03", before.SevenZipInstallVersion);
        Assert.True(before.AutoInstall);
        Assert.Equal(toolsFolder, before.InstallFolder);

        var response = await client.PostAsync("/api/v1/settings/tools/par2/install", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var installed = (await response.Content.ReadFromJsonAsync<ToolSettingsResource>())!;
        Assert.True(installed.Par2Found);
        Assert.Equal("Installed", installed.Par2Source);
        Assert.Equal(Path.Combine(toolsFolder, "par2", par2.Executable), installed.Par2ResolvedPath);
        Assert.Equal("1.5.0", installed.Par2InstalledVersion);
        Assert.False(installed.Par2Installing);

        // The 7-Zip download is not there.
        var failed = await client.PostAsync("/api/v1/settings/tools/sevenZip/install", null);
        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        var problem = await failed.Content.ReadAsStringAsync();
        Assert.Contains("7-Zip could not be installed", problem);
        Assert.Contains("HTTP 404", problem);
        var after = (await client.GetFromJsonAsync<ToolSettingsResource>("/api/v1/settings/tools"))!;
        Assert.Contains("HTTP 404", after.SevenZipInstallError);
        Assert.Null(after.SevenZipInstalledVersion);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/settings/tools/ffmpeg/install", null)).StatusCode);
    }

    [Fact]
    public async Task Auto_install_can_be_switched_off()
    {
        var client = await _factory.CreateOwnerClientAsync();

        var saved = await client.PutAsJsonAsync("/api/v1/settings/tools", new { autoInstall = false });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.False((await saved.Content.ReadFromJsonAsync<ToolSettingsResource>())!.AutoInstall);
        Assert.False((await client.GetFromJsonAsync<ToolSettingsResource>("/api/v1/settings/tools"))!.AutoInstall);

        var on = await client.PutAsJsonAsync("/api/v1/settings/tools", new { autoInstall = true });
        Assert.True((await on.Content.ReadFromJsonAsync<ToolSettingsResource>())!.AutoInstall);
    }

    [Fact]
    public async Task Tools_that_cannot_be_installed_here_are_refused()
    {
        _factory.ToolCatalog = new ToolCatalog((_, _) => null);
        var client = await _factory.CreateOwnerClientAsync();

        var settings = (await client.GetFromJsonAsync<ToolSettingsResource>("/api/v1/settings/tools"))!;
        Assert.False(settings.Par2Installable);
        Assert.Null(settings.Par2InstallVersion);

        var response = await client.PostAsync("/api/v1/settings/tools/par2/install", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("cannot install par2", await response.Content.ReadAsStringAsync());
        Assert.Empty(_factory.ToolDownloads.Requests);
    }

    [Fact]
    public async Task Tool_settings_require_login()
    {
        await _factory.CreateOwnerClientAsync();
        var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/settings/tools")).StatusCode);
    }

    [Fact]
    public void Post_processor_is_registered()
    {
        Assert.IsType<PostProcessor>(_factory.Services.GetRequiredService<IPostProcessor>());
    }

    public void Dispose() => _factory.Dispose();
}
