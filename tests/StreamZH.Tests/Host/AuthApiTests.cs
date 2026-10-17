// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using StreamZH.Host.Api;

namespace StreamZH.Tests.Host;

public sealed class AuthApiTests : IDisposable
{
    private readonly StreamZhFactory _factory = new();

    [Fact]
    public async Task Fresh_install_requires_setup_and_protects_the_api()
    {
        var client = _factory.CreateClient();

        var state = await client.GetFromJsonAsync<AuthState>("/api/v1/auth/state");
        Assert.True(state!.SetupRequired);
        Assert.False(state.Authenticated);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/system/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Setup_creates_owner_logs_in_and_runs_only_once()
    {
        var client = await _factory.CreateOwnerClientAsync();

        var state = await client.GetFromJsonAsync<AuthState>("/api/v1/auth/state");
        Assert.False(state!.SetupRequired);
        Assert.True(state.Authenticated);
        Assert.Equal("Owner", state.User!.Role);

        var status = await client.GetFromJsonAsync<SystemStatus>("/api/v1/system/status");
        Assert.Equal("StreamZH", status!.AppName);
        Assert.True(File.Exists(Path.Combine(_factory.DataDirectory, "streamzh.db")));

        var second = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/setup", new { username = "intruder", password = "12345678910" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Theory]
    [InlineData("jo", "long enough password")]
    [InlineData("john doe", "long enough password")]
    [InlineData("johndoe", "short")]
    public async Task Setup_validates_input(string username, string password)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/setup", new { username, password });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Logout_and_login()
    {
        var client = await _factory.CreateOwnerClientAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/system/status")).StatusCode);

        var wrong = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = StreamZhFactory.OwnerName, password = "wrong password" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        // Username is case-insensitive.
        var ok = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "JohnDoe", password = StreamZhFactory.OwnerPassword, rememberMe = true });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/system/status")).StatusCode);
    }

    [Fact]
    public async Task Api_key_header_authenticates_scripts()
    {
        var client = _factory.CreateClient();

        client.DefaultRequestHeaders.Add("X-Api-Key", "not-the-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/system/status")).StatusCode);

        client.DefaultRequestHeaders.Remove("X-Api-Key");
        client.DefaultRequestHeaders.Add("X-Api-Key", _factory.ApiKey);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/system/status")).StatusCode);
    }

    [Fact]
    public async Task Unknown_api_route_is_404_but_app_routes_serve_the_ui()
    {
        var client = await _factory.CreateOwnerClientAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/does-not-exist")).StatusCode);
    }

    public void Dispose() => _factory.Dispose();
}