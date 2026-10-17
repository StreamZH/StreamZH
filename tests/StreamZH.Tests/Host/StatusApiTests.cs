// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Host.Auth;

namespace StreamZH.Tests.Host;

public sealed class StatusApiTests : IDisposable
{
    private readonly StreamZhFactory _factory = new();

    [Fact]
    public async Task Ongoing_is_for_administrators_only()
    {
        var owner = await _factory.CreateOwnerClientAsync();

        var response = await owner.GetAsync("/api/v1/status/ongoing");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("running").ValueKind);
        var notice = Assert.Single(json.RootElement.GetProperty("notices").EnumerateArray());
        Assert.Equal("no-indexer", notice.GetProperty("key").GetString());
        Assert.Equal("warn", notice.GetProperty("level").GetString());
        Assert.False(notice.GetProperty("dismissible").GetBoolean());

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/v1/status/ongoing")).StatusCode);

        var users = _factory.Services.GetRequiredService<UserRepository>();
        users.Create("friend", _factory.Services.GetRequiredService<PasswordService>().Hash("friendly password"), UserRole.Friend);
        var friend = _factory.CreateClient();
        (await friend.PostAsJsonAsync("/api/v1/auth/login", new { username = "friend", password = "friendly password" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.GetAsync("/api/v1/status/ongoing")).StatusCode);
    }

    public void Dispose() => _factory.Dispose();
}
