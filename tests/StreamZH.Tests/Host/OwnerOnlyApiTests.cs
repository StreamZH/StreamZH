// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Media;
using StreamZH.Core.Users;
using StreamZH.Data;

namespace StreamZH.Tests.Host;

/// <summary>
/// 0.12 (S1-03, S3-05, S3-06, S2-2): the API key acts as an Admin, and an Admin cannot become the owner: no API key,
/// no security, proxy or backup settings, no backups, no tool paths or tool parameters.
/// </summary>
public sealed class OwnerOnlyApiTests : IDisposable
{
    private readonly StreamZhFactory _factory = new();
    private readonly string _scratch = Directory.CreateTempSubdirectory("streamzh-owner-only-").FullName;

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_scratch, recursive: true);
    }

    private static async Task<JsonElement> ErrorsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
    }

    [Fact]
    public async Task The_api_key_is_an_admin_and_cannot_read_itself_or_do_owner_only_things()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        var api = _factory.CreateApiKeyClient();

        var state = await api.GetFromJsonAsync<JsonElement>("/api/v1/auth/state");
        Assert.Equal("Admin", state.GetProperty("user").GetProperty("role").GetString());
        var me = await api.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.Equal("admin", me.GetProperty("role").GetString());

        // The exploit of S3-05: read the key as Admin, then use it as the owner.
        var general = await api.GetFromJsonAsync<JsonElement>("/api/v1/settings/general");
        Assert.Equal("********", general.GetProperty("apiKey").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await api.PostAsync("/api/v1/settings/apikey", null)).StatusCode);

        var external = await ErrorsAsync(await api.PutAsJsonAsync("/api/v1/settings/general", new { authenticationMethod = "external" }));
        Assert.Equal("Only the owner can change this.", external.GetProperty("authenticationMethod")[0].GetString());
        Assert.True((await ErrorsAsync(await api.PutAsJsonAsync("/api/v1/settings/general", new { authenticationRequired = "disabledForLocalAddresses" })))
            .TryGetProperty("authenticationRequired", out _));
        Assert.True((await ErrorsAsync(await api.PutAsJsonAsync("/api/v1/settings/general", new { certificateValidation = "disabled" })))
            .TryGetProperty("certificateValidation", out _));
        var stored = await owner.GetFromJsonAsync<JsonElement>("/api/v1/settings/general");
        Assert.Equal(("forms", "enabled", "enabled"), (
            stored.GetProperty("authenticationMethod").GetString(),
            stored.GetProperty("authenticationRequired").GetString(),
            stored.GetProperty("certificateValidation").GetString()));

        // Backups hold every secret and the key ring; a restore replaces the accounts.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.GetAsync("/api/v1/system/backup")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.PostAsync("/api/v1/system/backup", null)).StatusCode);
        using var zip = new ByteArrayContent([0x50, 0x4b, 0x03, 0x04]);
        zip.Headers.ContentType = new("application/zip");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.PostAsync("/api/v1/system/backup/restore?name=x.zip", zip)).StatusCode);

        // StreamZH runs the configured tools.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.PutAsJsonAsync("/api/v1/settings/tools", new { par2Path = "/bin/sh" })).StatusCode);

        // Everything else an Admin may do still works for scripts and CloudZH.
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/v1/indexer")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/v1/settings/tools")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsJsonAsync("/api/v1/settings/general", new { instanceName = "Media" })).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await api.PutAsJsonAsync("/api/v1/settings/general", new { authenticationMethod = "forms", authenticationRequired = "enabled", certificateValidation = "enabled" })).StatusCode);
    }

    [Fact]
    public async Task An_admin_account_cannot_change_proxy_backups_or_tool_parameters_but_the_owner_can()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        var admin = await _factory.CreateUserClientAsync("admin1", UserRole.Admin);

        Assert.Equal("********", (await admin.GetFromJsonAsync<JsonElement>("/api/v1/settings/general")).GetProperty("apiKey").GetString());
        Assert.Equal(_factory.ApiKey, (await owner.GetFromJsonAsync<JsonElement>("/api/v1/settings/general")).GetProperty("apiKey").GetString());

        // The proxy sees the traffic with the indexer keys; the backup folder would receive every secret.
        var proxy = await ErrorsAsync(await admin.PutAsJsonAsync("/api/v1/settings/general", new { proxyEnabled = true, proxyHostname = "proxy.example", proxyPort = 3128 }));
        Assert.True(proxy.TryGetProperty("proxyEnabled", out _) && proxy.TryGetProperty("proxyHostname", out _) && proxy.TryGetProperty("proxyPort", out _));
        var folder = Directory.CreateDirectory(Path.Combine(_scratch, "share")).FullName;
        Assert.True((await ErrorsAsync(await admin.PutAsJsonAsync("/api/v1/settings/general", new { backupFolder = folder }))).TryGetProperty("backupFolder", out _));
        Assert.True((await ErrorsAsync(await admin.PutAsJsonAsync("/api/v1/settings/general", new { backupInterval = 1 }))).TryGetProperty("backupInterval", out _));

        // Tool parameters go to programs StreamZH runs.
        var args = await ErrorsAsync(await admin.PutAsJsonAsync("/api/v1/settings/downloads", new { par2Arguments = "-t4", sevenZipArguments = "-mmt2" }));
        Assert.True(args.TryGetProperty("par2Arguments", out _) && args.TryGetProperty("sevenZipArguments", out _));
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/settings/downloads", new { par2Arguments = "", articleTries = 3 })).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/system/backup")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync("/api/v1/settings/apikey", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync("/api/v1/settings/tools", new { autoInstall = false })).StatusCode);

        // The owner can.
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/settings/general", new { backupFolder = folder, backupInterval = 1, certificateValidation = "disabledForLocalAddresses" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/settings/downloads", new { par2Arguments = "-t4", sevenZipArguments = "-mmt2" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/system/backup")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/settings/tools", new { autoInstall = false })).StatusCode);

        // Saving a section unchanged is fine for the Admin (the page sends whole sections).
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/settings/general", new { backupFolder = folder, backupInterval = 1, backupRetention = 28 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/settings/downloads", new { par2Arguments = "-t4", sevenZipArguments = "-mmt2" })).StatusCode);
    }

    [Fact]
    public async Task A_configured_tool_must_be_named_like_the_tool()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var owner = await _factory.CreateOwnerClientAsync();

        // S3-02: any existing program was accepted and run (version probe) as the service account.
        var shell = await ErrorsAsync(await owner.PutAsJsonAsync("/api/v1/settings/tools", new { par2Path = "/bin/sh" }));
        Assert.Contains("only runs a file named", shell.GetProperty("par2Path")[0].GetString(), StringComparison.Ordinal);
        var script = Path.Combine(_scratch, "evil.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\n");
        Assert.True((await ErrorsAsync(await owner.PutAsJsonAsync("/api/v1/settings/tools", new { sevenZipPath = script, ffprobePath = script })))
            .TryGetProperty("ffprobePath", out _));

        var par2 = Path.Combine(Directory.CreateDirectory(Path.Combine(_scratch, "bin")).FullName, "par2");
        await File.WriteAllTextAsync(par2, string.Empty);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/settings/tools", new { par2Path = par2 })).StatusCode);
        var tools = await owner.GetFromJsonAsync<JsonElement>("/api/v1/settings/tools");
        Assert.Equal(par2, tools.GetProperty("par2ResolvedPath").GetString());

        // A path stored by an earlier version that is not named like the tool is not used.
        _factory.Services.GetRequiredService<ConfigRepository>().Set(ConfigKeys.SevenZipPath, "/bin/sh");
        tools = await owner.GetFromJsonAsync<JsonElement>("/api/v1/settings/tools");
        Assert.NotEqual("/bin/sh", tools.GetProperty("sevenZipResolvedPath").GetString());
    }

    [Fact]
    public async Task History_links_lose_their_keys_and_friends_never_see_the_download_link()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        var friend = await _factory.CreateUserClientAsync("friend1", UserRole.Friend);
        _factory.Services.GetRequiredService<HistoryRepository>().Add(new HistoryItem
        {
            Date = DateTime.UtcNow,
            EventType = HistoryEventType.Grabbed,
            MediaType = MediaType.Movie,
            SourceTitle = "Some.Movie.2024.1080p.WEB-DL-GRP",
            Indexer = "Indexer",
            Data = new Dictionary<string, string?>
            {
                ["downloadUrl"] = "https://indexer.example/getnzb/abc.nzb&i=1&r=0123456789abcdef",
                ["guid"] = "https://indexer.example/api?t=get&id=abc&apikey=0123456789abcdef",
            },
        });

        var ownerView = await owner.GetStringAsync("/api/v1/history");
        Assert.DoesNotContain("0123456789abcdef", ownerView, StringComparison.Ordinal);
        var record = JsonDocument.Parse(ownerView).RootElement.GetProperty("records")[0].GetProperty("data");
        Assert.Equal("https://indexer.example/getnzb/abc.nzb&i=1&r=(removed)", record.GetProperty("downloadUrl").GetString());
        Assert.Equal("https://indexer.example/api?t=get&id=abc&apikey=(removed)", record.GetProperty("guid").GetString());

        var friendView = await friend.GetStringAsync("/api/v1/history");
        Assert.DoesNotContain("0123456789abcdef", friendView, StringComparison.Ordinal);
        Assert.False(JsonDocument.Parse(friendView).RootElement.GetProperty("records")[0].GetProperty("data").TryGetProperty("downloadUrl", out _));

        // Stored without the key, too.
        var raw = _factory.Services.GetRequiredService<Database>();
        using var connection = raw.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Data FROM History";
        Assert.DoesNotContain("0123456789abcdef", (string)command.ExecuteScalar()!, StringComparison.Ordinal);
    }
}
