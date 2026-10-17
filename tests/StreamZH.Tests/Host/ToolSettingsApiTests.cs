// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Downloads;
using StreamZH.Host.Api;
using StreamZH.Library.PostProcessing;

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
