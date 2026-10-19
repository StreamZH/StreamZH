// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Library.Discord;

namespace StreamZH.Tests.Discord;

public sealed class RequestrrImportTests : IDisposable
{
    /// <summary>A Requestrr 2.x config/settings.json with made-up ids and a fake token.</summary>
    public const string Fixture = """
        {
          "Authentication": { "Username": "admin", "Password": "not-a-real-hash", "PrivateKey": "not-a-real-key" },
          "ChatClients": {
            "Discord": {
              "BotToken": "fake-token-part-one-000000000000.Fake00.this-is-not-a-real-discord-bot-token-000",
              "ClientId": "100000000000000098",
              "StatusMessage": "/help for requests",
              "TvShowRoles": ["100000000000000030", "100000000000000032"],
              "MovieRoles": ["100000000000000030"],
              "MusicRoles": [],
              "MonitoredChannels": ["100000000000000020", 100000000000000021, "requests"],
              "EnableRequestsThroughDirectMessages": true,
              "AutomaticallyNotifyRequesters": true,
              "NotificationMode": "Channels",
              "NotificationChannels": ["100000000000000040", "100000000000000041"],
              "AutomaticallyPurgeCommandMessages": true
            },
            "Language": "english"
          },
          "DownloadClients": { "Radarr": { "Hostname": "192.168.1.10", "Port": 7878, "ApiKey": "not-a-real-key" } },
          "BotClient": { "Client": "Discord" },
          "Movies": { "Client": "Radarr" },
          "Music": { "Client": "Disabled" },
          "TvShows": { "Client": "Disabled", "Restrictions": "SingleSeason" },
          "Port": 4545,
          "BaseUrl": "",
          "DisableAuthentication": false,
          "Version": "2.1.3"
        }
        """;

    private readonly DiscordTestBed _bed = new();

    public void Dispose() => _bed.Dispose();

    [Fact]
    public void Maps_channels_roles_notifications_and_restrictions()
    {
        var preview = RequestrrImport.Preview(Fixture, _bed.Settings.Values, takeToken: false);
        var change = preview.Change;

        Assert.Equal("2.1.3", preview.Version);
        Assert.True(preview.HasToken);
        Assert.Null(change.BotToken);
        Assert.Equal("100000000000000098", change.ApplicationId);
        Assert.Equal(["100000000000000020", "100000000000000021"], change.ChannelIds);
        Assert.True(change.AllowGuests);
        Assert.Equal(["100000000000000030", "100000000000000032"], change.GuestRoleIds);
        Assert.Equal("100000000000000040", change.NotifyChannelId);
        Assert.False(change.DmRequesters);
        Assert.True(change.AllowDirectMessages);
        Assert.Null(change.MovieCommand);
        Assert.Equal(string.Empty, change.TvCommand);
        Assert.Equal(DiscordSeasonModes.Single, change.SeriesSeasons);
        Assert.Equal("/help for requests", change.StatusText);

        Assert.Contains(preview.Warnings, w => w.Contains("\"requests\" is not a monitored channel id", StringComparison.Ordinal));
        Assert.Contains(preview.Warnings, w => w.Contains("2 notification channels", StringComparison.Ordinal));
        Assert.Contains(preview.Warnings, w => w.Contains("different roles", StringComparison.Ordinal));
        Assert.Contains(preview.Warnings, w => w.Contains("not taken over", StringComparison.Ordinal));
        Assert.DoesNotContain(preview.Rows, r => r.Field == "botToken");
        Assert.Contains(preview.Rows, r => r.Field == "tvCommand" && r.Imported == "off");

        // Nothing in the preview shows the token or Requestrr's other secrets.
        var text = string.Join('\n', preview.Rows.Select(r => $"{r.Current} {r.Imported}").Concat(preview.Warnings));
        Assert.DoesNotContain("fake-token", text, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-real", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_token_is_taken_over_only_when_asked_and_the_result_is_valid()
    {
        var preview = RequestrrImport.Preview(Fixture, _bed.Settings.Values, takeToken: true);

        Assert.Equal(DiscordTestBed.Token, preview.Change.BotToken);
        Assert.Contains(preview.Warnings, w => w.Contains("Stop Requestrr", StringComparison.Ordinal));
        Assert.Empty(_bed.Settings.Validate(preview.Change));

        _bed.Settings.Update(preview.Change);
        Assert.True(_bed.Settings.Values.HasToken);
        Assert.Equal(DiscordSeasonModes.Single, _bed.Settings.Values.SeriesSeasons);
    }

    [Fact]
    public void Private_message_notifications_and_older_files_are_understood()
    {
        const string old = """
            { "ChatClients": { "discord": { "botToken": "", "MonitoredChannels": "100000000000000020 100000000000000021",
              "NotificationMode": "PrivateMessages", "AutomaticallyNotifyRequesters": true } }, "Version": "1.0.9" }
            """;
        var preview = RequestrrImport.Preview(old, _bed.Settings.Values, takeToken: true);

        Assert.False(preview.HasToken);
        Assert.Null(preview.Change.BotToken);
        Assert.Equal(["100000000000000020", "100000000000000021"], preview.Change.ChannelIds);
        Assert.True(preview.Change.DmRequesters);
        Assert.Equal(string.Empty, preview.Change.NotifyChannelId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{ "Radarr": {} }""")]
    [InlineData("[1, 2]")]
    public void Other_files_are_refused_with_a_clear_message(string json)
    {
        var error = Assert.Throws<RequestrrImportException>(() => RequestrrImport.Preview(json, _bed.Settings.Values, false));
        Assert.Contains("settings.json", error.Message, StringComparison.Ordinal);
    }
}
