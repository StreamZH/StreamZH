// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using StreamZH.Host.Api;

namespace StreamZH.Tests.Host;

public sealed class SettingsApiTests : IDisposable
{
    private readonly StreamZhFactory _factory = new();

    [Fact]
    public async Task Reads_and_updates_general_settings()
    {
        var client = await _factory.CreateOwnerClientAsync();

        var settings = await client.GetFromJsonAsync<GeneralSettings>("/api/v1/settings/general");
        Assert.Equal("StreamZH", settings!.InstanceName);
        Assert.Equal("Information", settings.LogLevel);
        Assert.Equal(32, settings.ApiKey.Length);

        var invalid = await client.PutAsJsonAsync("/api/v1/settings/general", new { instanceName = "", logLevel = "Loud" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var saved = await client.PutAsJsonAsync("/api/v1/settings/general", new { instanceName = "John's Server", logLevel = "Debug" });
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

        settings = await client.GetFromJsonAsync<GeneralSettings>("/api/v1/settings/general");
        Assert.Equal("John's Server", settings!.InstanceName);
        Assert.Equal("Debug", settings.LogLevel);

        var state = await client.GetFromJsonAsync<AuthState>("/api/v1/auth/state");
        Assert.Equal("John's Server", state!.InstanceName);
    }

    [Fact]
    public async Task Regenerating_the_api_key_invalidates_the_old_one()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var oldKey = _factory.ApiKey;

        var response = await client.PostAsync("/api/v1/settings/apikey", null);
        var newKey = (await response.Content.ReadFromJsonAsync<ApiKeyResponse>())!.ApiKey;
        Assert.NotEqual(oldKey, newKey);

        var script = _factory.CreateClient();
        script.DefaultRequestHeaders.Add("X-Api-Key", oldKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await script.GetAsync("/api/v1/system/status")).StatusCode);

        script.DefaultRequestHeaders.Remove("X-Api-Key");
        script.DefaultRequestHeaders.Add("X-Api-Key", newKey);
        Assert.Equal(HttpStatusCode.OK, (await script.GetAsync("/api/v1/system/status")).StatusCode);
    }

    [Fact]
    public async Task Settings_require_login()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/settings/general")).StatusCode);
    }

    [Fact]
    public async Task Log_viewer_returns_recent_lines()
    {
        var client = await _factory.CreateOwnerClientAsync();
        await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "johndoe", password = "wrong password" });

        var logs = await client.GetFromJsonAsync<LogTail>("/api/v1/system/logs?lines=50");

        Assert.Contains(logs!.Lines, l => l.Contains("started", StringComparison.Ordinal));
        Assert.Contains(logs.Lines, l => l.Contains("|Warn|", StringComparison.Ordinal) && l.Contains("Failed login", StringComparison.Ordinal));
    }

    public void Dispose() => _factory.Dispose();
}