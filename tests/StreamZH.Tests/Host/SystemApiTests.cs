// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using StreamZH.Host.Api;

namespace StreamZH.Tests.Host;

public sealed class SystemApiTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"streamzh-host-{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> _factory;

    public SystemApiTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("data", _dataDir));
    }

    [Fact]
    public async Task Status_reports_version_and_schema()
    {
        var client = _factory.CreateClient();

        var status = await client.GetFromJsonAsync<SystemStatus>("/api/v1/system/status");

        Assert.NotNull(status);
        Assert.Equal("StreamZH", status.AppName);
        Assert.Equal(1, status.DatabaseSchemaVersion);
        Assert.True(File.Exists(Path.Combine(_dataDir, "streamzh.db")));
    }

    [Fact]
    public async Task Health_is_ok() =>
        Assert.True((await _factory.CreateClient().GetAsync("/health")).IsSuccessStatusCode);

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }
}