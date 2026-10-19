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
using StreamZH.Library.Search;

namespace StreamZH.Tests.Host;

public sealed class AutomationApiTests : IDisposable
{
    private readonly StreamZhFactory _factory = new();

    [Fact]
    public async Task Side_by_side_mode_is_read_by_everyone_and_switched_by_admins()
    {
        var owner = await _factory.CreateOwnerClientAsync();

        var off = await owner.GetFromJsonAsync<JsonElement>("/api/v1/settings/automation");
        Assert.False(off.GetProperty("hold").GetBoolean());
        Assert.Equal(JsonValueKind.Null, off.GetProperty("since").ValueKind);

        var response = await owner.PutAsJsonAsync("/api/v1/settings/automation", new { hold = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var on = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(on.GetProperty("hold").GetBoolean());
        Assert.True(on.GetProperty("since").GetDateTime() > DateTime.UtcNow.AddMinutes(-5));
        Assert.True(_factory.Services.GetRequiredService<AutomationHold>().IsOn);

        var invalid = await owner.PutAsJsonAsync("/api/v1/settings/automation", new { });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("hold", await invalid.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var users = _factory.Services.GetRequiredService<UserRepository>();
        users.Create("friend", _factory.Services.GetRequiredService<PasswordService>().Hash("friendly password"), UserRole.Friend);
        var friend = _factory.CreateClient();
        (await friend.PostAsJsonAsync("/api/v1/auth/login", new { username = "friend", password = "friendly password" })).EnsureSuccessStatusCode();
        Assert.True((await friend.GetFromJsonAsync<JsonElement>("/api/v1/settings/automation")).GetProperty("hold").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.PutAsJsonAsync("/api/v1/settings/automation", new { hold = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/v1/settings/automation")).StatusCode);

        var back = await (await owner.PutAsJsonAsync("/api/v1/settings/automation", new { hold = false })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(back.GetProperty("hold").GetBoolean());
        Assert.Equal("false", _factory.Services.GetRequiredService<ConfigRepository>().Get(ConfigKeys.AutomationHold));
    }

    [Fact]
    public async Task The_status_window_shows_the_mode_without_counting_it_as_a_warning()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        var before = await owner.GetFromJsonAsync<JsonElement>("/api/v1/status/ongoing");
        Assert.False(before.GetProperty("automationHold").GetBoolean());
        Assert.DoesNotContain(before.GetProperty("notices").EnumerateArray(), n => n.GetProperty("key").GetString() == "automation-hold");

        (await owner.PutAsJsonAsync("/api/v1/settings/automation", new { hold = true })).EnsureSuccessStatusCode();

        var after = await owner.GetFromJsonAsync<JsonElement>("/api/v1/status/ongoing");
        Assert.True(after.GetProperty("automationHold").GetBoolean());
        var notice = Assert.Single(after.GetProperty("notices").EnumerateArray(), n => n.GetProperty("key").GetString() == "automation-hold");
        Assert.Equal("info", notice.GetProperty("level").GetString());
        Assert.Equal("Side-by-side mode: StreamZH grabs nothing automatically", notice.GetProperty("title").GetString());

        // System → Status lists it as well.
        var info = await owner.GetFromJsonAsync<JsonElement>("/api/v1/system/info");
        Assert.Contains(info.GetProperty("health").EnumerateArray(), n => n.GetProperty("key").GetString() == "automation-hold");
    }

    [Fact]
    public async Task Migration_sources_list_the_sabnzbd_and_indexer_parts()
    {
        var owner = await _factory.CreateOwnerClientAsync();

        var sources = await owner.GetFromJsonAsync<JsonElement>("/api/v1/migration/sources");
        string[] Keys(string source) =>
            [.. sources.EnumerateArray().Single(s => s.GetProperty("key").GetString() == source).GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("key").GetString()!)];

        Assert.Equal(["servers", "categories", "downloads"], Keys("sabnzbd"));
        Assert.Contains("indexers", Keys("prowlarr"));
        Assert.Contains("indexers", Keys("radarr"));
        Assert.Contains("downloadClients", Keys("sonarr"));

        // SABnzbd that cannot be reached: a failed test, not an error.
        var test = await owner.PostAsJsonAsync("/api/v1/migration/sabnzbd/test", new { url = "http://127.0.0.1:1", apiKey = "x" });
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        var result = await test.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains("SABnzbd", result.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    public void Dispose() => _factory.Dispose();
}
