// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using StreamZH.Library.Notifications;

namespace StreamZH.Tests.Connect;

public sealed class ConnectionSettingsTests
{
    private static Dictionary<string, JsonElement> Input(object values) =>
        JsonSerializer.SerializeToElement(values).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    [Fact]
    public void Defaults_fill_every_field_of_the_type()
    {
        var plex = ConnectionSettings.Defaults(ConnectCatalog.Find("plex")!);
        var settings = new ConnectionSettings(plex);

        Assert.Equal(32400, settings.Int("port"));
        Assert.True(settings.Bool("updateLibrary"));
        Assert.False(settings.Bool("useSsl"));
        Assert.Null(settings.Text("authToken"));

        var discord = new ConnectionSettings(ConnectionSettings.Defaults(ConnectCatalog.Find("discord")!));
        Assert.Equal(["overview", "quality", "group", "size", "indexer", "links", "poster"], discord.List("grabFields"));
    }

    [Fact]
    public void Secrets_are_masked_and_the_mask_keeps_the_stored_value()
    {
        var type = ConnectCatalog.Find("pushover")!;
        var errors = new Dictionary<string, string[]>();
        var stored = ConnectionSettings.Normalize(type, Input(new { apiKey = "app-token", userKey = "user-key" }), null, errors);
        Assert.Empty(errors);

        var resource = ConnectionSettings.ToResource(type, stored);
        Assert.Equal(ConnectionSettings.Mask, resource["apiKey"]);
        Assert.Equal(ConnectionSettings.Mask, resource["userKey"]);
        Assert.Equal("0", resource["priority"]);

        var saved = ConnectionSettings.Normalize(type, Input(new { apiKey = ConnectionSettings.Mask, userKey = "new-user", priority = "1" }), stored, errors);
        Assert.Empty(errors);
        var settings = new ConnectionSettings(saved);
        Assert.Equal("app-token", settings.Text("apiKey"));
        Assert.Equal("new-user", settings.Text("userKey"));
        Assert.Equal("1", settings.Text("priority"));

        var empty = ConnectionSettings.ToResource(type, new Dictionary<string, JsonElement>());
        Assert.Equal(string.Empty, empty["apiKey"]);
    }

    [Fact]
    public void Fields_not_sent_keep_their_stored_value_and_null_clears_them()
    {
        var type = ConnectCatalog.Find("plex")!;
        var errors = new Dictionary<string, string[]>();
        var stored = ConnectionSettings.Normalize(type, Input(new { host = "plex", authToken = "t", port = 1234, updateLibrary = false }), null, errors);

        var kept = new ConnectionSettings(ConnectionSettings.Normalize(type, Input(new { updateLibrary = true }), stored, errors));
        Assert.Equal(("plex", 1234, true, "t"), (kept.Text("host"), kept.Int("port"), kept.Bool("updateLibrary"), kept.Text("authToken")));

        var cleared = new ConnectionSettings(ConnectionSettings.Normalize(type, Input(new { mapFrom = (string?)null }), stored, errors));
        Assert.Null(cleared.Text("mapFrom"));
        Assert.Empty(errors);

        // V-01: a secret that is not sent is a reused one; it does not go to another host either.
        var moved = new ConnectionSettings(ConnectionSettings.Normalize(type, Input(new { host = "other" }), stored, errors));
        Assert.Null(moved.Text("authToken"));
        Assert.Equal(["Enter the auth token again to save a changed address."], errors["authToken"]);
    }

    [Fact]
    public void Invalid_values_are_reported_by_field()
    {
        var errors = new Dictionary<string, string[]>();
        ConnectionSettings.Normalize(
            ConnectCatalog.Find("plex")!,
            Input(new { host = "", port = 70000, useSsl = "maybe", authToken = "" }),
            null,
            errors);

        Assert.Equal(["Host is required."], errors["host"]);
        Assert.Equal(["Use 1 to 65535."], errors["port"]);
        Assert.Equal(["Use true or false."], errors["useSsl"]);
        Assert.Equal(["Auth token is required."], errors["authToken"]);

        errors.Clear();
        ConnectionSettings.Normalize(
            ConnectCatalog.Find("discord")!,
            Input(new { webHookUrl = "x", avatar = "ftp://host/a.png", grabFields = new[] { "quality", "colour" } }),
            null,
            errors);
        Assert.Equal(["Enter a full URL starting with http:// or https://."], errors["avatar"]);
        Assert.StartsWith("Use any of:", errors["grabFields"][0], StringComparison.Ordinal);

        errors.Clear();
        ConnectionSettings.Normalize(ConnectCatalog.Find("pushover")!, Input(new { apiKey = "a", userKey = "b", priority = "7", retry = "abc" }), null, errors);
        Assert.StartsWith("Use one of:", errors["priority"][0], StringComparison.Ordinal);
        Assert.Equal(["Use 30 to 10800."], errors["retry"]);
    }

    [Fact]
    public void Lists_accept_commas_and_arrays_and_numbers_accept_strings()
    {
        var errors = new Dictionary<string, string[]>();
        var values = ConnectionSettings.Normalize(ConnectCatalog.Find("ntfy")!, Input(new { topics = " a, b ,,c ", priority = "5" }), null, errors);
        var settings = new ConnectionSettings(values);

        Assert.Equal(["a", "b", "c"], settings.List("topics"));
        Assert.Equal("5", settings.Text("priority"));

        var plex = new ConnectionSettings(ConnectionSettings.Normalize(ConnectCatalog.Find("plex")!, Input(new { host = "h", authToken = "t", port = "32401" }), null, errors));
        Assert.Equal(32401, plex.Int("port"));
        Assert.Empty(errors);
    }

    [Fact]
    public void Catalog_is_consistent()
    {
        Assert.Equal(12, ConnectCatalog.All.Count);
        Assert.Equal(ConnectCatalog.All.Count, ConnectCatalog.All.Select(t => t.Key).Distinct().Count());
        foreach (var type in ConnectCatalog.All)
        {
            Assert.Equal(type.Fields.Count, type.Fields.Select(f => f.Name).Distinct().Count());
            Assert.All(type.Triggers, t => Assert.Contains(t, ConnectTriggers.All));
            Assert.All(ConnectTriggers.All.Where(t => ConnectTriggers.IsOn(type.Defaults, t)), t => Assert.Contains(t, type.Triggers));
            Assert.DoesNotContain(type.Fields, f => f.Name is "name" or "enable" or "implementation");
            Assert.All(type.Fields.Where(f => f.Type is ConnectFieldTypes.Select or ConnectFieldTypes.MultiSelect), f => Assert.NotEmpty(f.Options!));
        }

        // Media servers update libraries; only notification types react to every trigger.
        Assert.DoesNotContain(ConnectTriggers.Grab, ConnectCatalog.Find("plex")!.Triggers);
        Assert.Equal(ConnectTriggers.All, ConnectCatalog.Find("discord")!.Triggers);
    }
}
