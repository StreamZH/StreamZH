// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Library.Discord;
using StreamZH.Tests.Host;

namespace StreamZH.Tests.Discord;

public sealed class DiscordSettingsApiTests : IDisposable
{
    private const string Url = "/api/v1/settings/discord";

    private readonly StreamZhFactory _root = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeDiscordApi _api = new();
    private readonly FakeGateway _gateway = new();

    public DiscordSettingsApiTests() =>
        _factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IDiscordApi>(_api));
            services.Replace(ServiceDescriptor.Singleton<IDiscordGateway>(_gateway));
        }));

    public void Dispose()
    {
        _factory.Dispose();
        _root.Dispose();
    }

    private async Task<HttpClient> OwnerAsync()
    {
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword })).EnsureSuccessStatusCode();
        return client;
    }

    private async Task<HttpClient> UserAsync(string name, UserRole role)
    {
        var passwords = _factory.Services.GetRequiredService<PasswordService>();
        _factory.Services.GetRequiredService<UserRepository>().Create(name, passwords.Hash("member password 123"), role);
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { username = name, password = "member password 123" })).EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task The_token_is_write_only_and_masked()
    {
        var owner = await OwnerAsync();
        var empty = await owner.GetFromJsonAsync<JsonElement>(Url);
        Assert.False(empty.GetProperty("hasToken").GetBoolean());
        Assert.Equal(string.Empty, empty.GetProperty("botToken").GetString());
        Assert.Equal("off", empty.GetProperty("status").GetProperty("state").GetString());
        // The requests module registers the real gateway (0.10).
        Assert.True(empty.GetProperty("requestsReady").GetBoolean());

        var saved = await owner.PutAsJsonAsync(Url, new { enabled = true, botToken = "Bot " + DiscordTestBed.Token, applicationId = FakeDiscordApi.ApplicationId });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var body = await saved.Content.ReadAsStringAsync();
        Assert.DoesNotContain(DiscordTestBed.Token, body, StringComparison.Ordinal);

        var read = await owner.GetFromJsonAsync<JsonElement>(Url);
        Assert.True(read.GetProperty("hasToken").GetBoolean());
        Assert.Equal(DiscordBotSettings.Mask, read.GetProperty("botToken").GetString());
        Assert.Contains($"client_id={FakeDiscordApi.ApplicationId}", read.GetProperty("inviteUrl").GetString(), StringComparison.Ordinal);
        Assert.Contains("scope=bot%20applications.commands", read.GetProperty("inviteUrl").GetString(), StringComparison.Ordinal);
        Assert.Equal(DiscordTestBed.Token, _factory.Services.GetRequiredService<DiscordBotSettings>().Token);

        // The mask keeps the token, the bot was started with it.
        (await owner.PutAsJsonAsync(Url, new { botToken = DiscordBotSettings.Mask, dmRequesters = false })).EnsureSuccessStatusCode();
        Assert.Equal(DiscordTestBed.Token, _factory.Services.GetRequiredService<DiscordBotSettings>().Token);
        await WaitFor(() => _gateway.Logins.Any(l => l.Token == DiscordTestBed.Token));

        // "" removes it, which is refused while the bot is on.
        var refused = await owner.PutAsJsonAsync(Url, new { botToken = string.Empty });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("botToken", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        (await owner.PutAsJsonAsync(Url, new { enabled = false, botToken = string.Empty })).EnsureSuccessStatusCode();
        Assert.Null(_factory.Services.GetRequiredService<DiscordBotSettings>().Token);
    }

    [Fact]
    public async Task Invalid_values_are_rejected_per_field()
    {
        var owner = await OwnerAsync();
        var response = await owner.PutAsJsonAsync(Url, new
        {
            botToken = "short",
            applicationId = "abc",
            guildIds = new[] { "100000000000000010", "my server" },
            notifyChannelId = "#general",
            movieCommand = "Movie Request",
            tvCommand = "help",
            seriesSeasons = "some",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        foreach (var field in new[] { "botToken", "applicationId", "guildIds", "notifyChannelId", "movieCommand", "tvCommand", "seriesSeasons" })
        {
            Assert.True(errors.TryGetProperty(field, out _), field);
        }
    }

    [Fact]
    public async Task Test_lists_servers_channels_and_roles_with_the_saved_or_a_new_token()
    {
        var owner = await OwnerAsync();
        var missing = await owner.PostAsJsonAsync(Url + "/test", new { });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var result = await (await owner.PostAsJsonAsync(Url + "/test", new { botToken = DiscordTestBed.Token })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal("StreamZH", result.GetProperty("botName").GetString());
        var guild = Assert.Single(result.GetProperty("guilds").EnumerateArray());
        Assert.Equal(["general", "requests"], guild.GetProperty("channels").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.Equal(["Friends"], guild.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("name").GetString()));

        _api.TokenError = new DiscordApiException("Discord rejected the bot token.", HttpStatusCode.Unauthorized);
        var failed = await (await owner.PostAsJsonAsync(Url + "/test", new { botToken = DiscordTestBed.Token })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(failed.GetProperty("ok").GetBoolean());
        Assert.Equal("Discord rejected the bot token.", failed.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Requestrr_import_previews_then_applies()
    {
        var owner = await OwnerAsync();
        var preview = await owner.PostAsJsonAsync(Url + "/import-requestrr", new { settings = RequestrrImportTests.Fixture });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var previewBody = await preview.Content.ReadAsStringAsync();
        Assert.DoesNotContain("fake-token", previewBody, StringComparison.Ordinal);
        var json = JsonDocument.Parse(previewBody).RootElement;
        Assert.False(json.GetProperty("applied").GetBoolean());
        Assert.True(json.GetProperty("hasToken").GetBoolean());
        Assert.False(json.GetProperty("takesToken").GetBoolean());
        Assert.Contains(json.GetProperty("rows").EnumerateArray(), r => r.GetProperty("field").GetString() == "channelIds");
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement>(Url)).GetProperty("channelIds").EnumerateArray());

        var applied = await (await owner.PostAsJsonAsync(Url + "/import-requestrr", new { settings = RequestrrImportTests.Fixture, apply = true, takeToken = true }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(applied.GetProperty("applied").GetBoolean());
        var settings = applied.GetProperty("settings");
        Assert.Equal(2, settings.GetProperty("channelIds").GetArrayLength());
        Assert.True(settings.GetProperty("hasToken").GetBoolean());
        Assert.Equal(DiscordBotSettings.Mask, settings.GetProperty("botToken").GetString());

        var broken = await owner.PostAsJsonAsync(Url + "/import-requestrr", new { settings = "{}" });
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
    }

    [Theory]
    [InlineData(UserRole.Friend)]
    [InlineData(UserRole.Requester)]
    public async Task Only_admins_reach_the_discord_settings(UserRole role)
    {
        await OwnerAsync();
        var member = await UserAsync("janedoe", role);

        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(Url + "/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync(Url, new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync(Url + "/test", new { botToken = DiscordTestBed.Token })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync(Url + "/import-requestrr", new { settings = "{}" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(Url)).StatusCode);
    }

    [Fact]
    public async Task A_stopped_bot_shows_up_in_the_status_window()
    {
        var owner = await OwnerAsync();
        _gateway.Script.Enqueue(FakeGateway.Fail(fatal: true));
        (await owner.PutAsJsonAsync(Url, new { enabled = true, botToken = DiscordTestBed.Token })).EnsureSuccessStatusCode();

        await WaitFor(() => _factory.Services.GetRequiredService<DiscordBotStatusHolder>().Current.State == DiscordBotStates.Error);
        var ongoing = await owner.GetFromJsonAsync<JsonElement>("/api/v1/status/ongoing");
        var notice = Assert.Single(ongoing.GetProperty("notices").EnumerateArray(), n => n.GetProperty("key").GetString() == "discord-bot");
        Assert.Equal("error", notice.GetProperty("level").GetString());
        Assert.Equal("/settings/discord", notice.GetProperty("link").GetString());
        Assert.Equal("error", (await owner.GetFromJsonAsync<JsonElement>(Url + "/status")).GetProperty("state").GetString());
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(20);
        }
    }
}
