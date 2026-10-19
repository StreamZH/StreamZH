// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using StreamZH.Core.Downloads;
using StreamZH.Core.Security;
using StreamZH.Data;
using StreamZH.Indexers.Newznab;
using StreamZH.Library.Downloads;
using StreamZH.Library.Notifications;
using StreamZH.Library.PostProcessing;
using StreamZH.Library.Search;

namespace StreamZH.Tests.Library;

/// <summary>0.12: URL redaction (S3-04), the destination rule for stored secrets (S3-03) and the tool restrictions (S3-02, S2-2).</summary>
public sealed class SecurityHelpersTests
{
    [Theory]
    [InlineData("https://indexer.example/getnzb/a.nzb&i=1&r=0123456789abcdef", "https://indexer.example/getnzb/a.nzb&i=1&r=(removed)")]
    [InlineData("https://indexer.example/api?t=get&id=a&apikey=KEY", "https://indexer.example/api?t=get&id=a&apikey=(removed)")]
    [InlineData("http://jackett:9117/dl/x/?jackett_apikey=KEY&path=abc&file=Some.Movie", "http://jackett:9117/dl/x/?jackett_apikey=(removed)&path=abc&file=Some.Movie")]
    [InlineData("http://prowlarr:9696/1/download?apikey=KEY&link=abc", "http://prowlarr:9696/1/download?apikey=(removed)&link=abc")]
    [InlineData("https://tracker.example/rss?passkey=KEY&API_KEY=KEY2&token=T", "https://tracker.example/rss?passkey=(removed)&API_KEY=(removed)&token=(removed)")]
    [InlineData("magnet:?xt=urn:btih:abc&tr=https%3A%2F%2Ftracker.example%2FKEY%2Fannounce", "magnet:?xt=urn:btih:abc&tr=(removed)")]
    [InlineData("https://user:secret@hooks.example.com/x", "https://user:(removed)@hooks.example.com/x")]
    [InlineData("abc123", "abc123")]
    [InlineData("", "")]
    public void Secret_url_parameters_are_removed(string url, string expected)
    {
        Assert.Equal(expected, SecretUrls.Redact(url));
        Assert.Equal(expected, SecretUrls.Redact(SecretUrls.Redact(url)));
    }

    [Fact]
    public void History_details_are_redacted_and_null_stays_null()
    {
        var data = HistoryRepository.RedactData(new Dictionary<string, string?> { ["downloadUrl"] = "https://a.example/x?apikey=K", ["nzb"] = null });
        Assert.Equal("https://a.example/x?apikey=(removed)", data["downloadUrl"]);
        Assert.Null(data["nzb"]);
    }

    [Fact]
    public void A_guid_that_is_the_download_link_loses_the_key_but_the_download_link_keeps_it()
    {
        var feed = NewznabRssParser.Parse(
            NewznabRssParser.Load("""
                <rss version="2.0" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/"><channel>
                  <item><title>Some.Movie.2024.1080p.WEB-DL-GRP</title>
                    <enclosure url="https://indexer.example/getnzb/aaa.nzb&amp;i=1&amp;r=SECRETKEY" length="1000" type="application/x-nzb"/>
                    <comments>https://indexer.example/details/aaa?apikey=SECRETKEY</comments>
                    <newznab:attr name="size" value="1000"/></item>
                </channel></rss>
                """),
            new Uri("https://indexer.example/api?t=search&apikey=SECRETKEY"));
        var release = Assert.Single(feed.Releases);
        Assert.Equal("https://indexer.example/getnzb/aaa.nzb&i=1&r=(removed)", release.Guid);
        Assert.Contains("SECRETKEY", release.DownloadUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRETKEY", release.InfoUrl ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Connection_secrets_are_reused_only_for_the_same_destination()
    {
        var plex = ConnectCatalog.Find("plex")!;
        var errors = new Dictionary<string, string[]>();
        var stored = ConnectionSettings.Normalize(plex, Input(new { host = "plex.local", authToken = "token" }), null, errors);
        Assert.Empty(errors);

        var same = ConnectionSettings.Normalize(plex, Input(new { host = "PLEX.local", authToken = ConnectionSettings.Mask, updateLibrary = false }), stored, errors);
        Assert.Empty(errors);
        Assert.Equal("token", same["authToken"].GetString());

        var moved = ConnectionSettings.Normalize(plex, Input(new { host = "attacker.example", authToken = ConnectionSettings.Mask }), stored, errors, test: true);
        Assert.False(moved.ContainsKey("authToken"));
        Assert.Equal(["Enter the auth token again to test a changed address."], errors["authToken"]);

        errors.Clear();
        ConnectionSettings.Normalize(plex, Input(new { port = 443, authToken = ConnectionSettings.Mask }), stored, errors);
        Assert.Equal(["Enter the auth token again to save a changed address."], errors["authToken"]);

        // A new value may go anywhere.
        errors.Clear();
        ConnectionSettings.Normalize(plex, Input(new { host = "other.example", authToken = "typed" }), stored, errors);
        Assert.Empty(errors);
    }

    [Fact]
    public void Every_connection_type_with_a_secret_names_where_it_goes_or_has_a_fixed_service()
    {
        // Types whose secret only ever goes to one fixed service (or is itself the address).
        string[] fixedServices = ["discord", "telegram", "pushover", "traktList", "plexWatchlist"];
        var types = ConnectCatalog.All.Select(t => (t.Key, t.Fields))
            .Concat(StreamZH.Library.ImportLists.ImportListCatalog.All.Select(t => (t.Key, t.AsConnectType().Fields)));
        foreach (var (key, fields) in types.Where(t => t.Fields.Any(f => f.Type == ConnectFieldTypes.Password)))
        {
            Assert.True(
                fixedServices.Contains(key) || fields.Any(f => ConnectionSettings.DestinationFields.Contains(f.Name)),
                $"{key} has a secret but no destination field");
        }
    }

    [Fact]
    public void Usenet_and_download_client_destinations()
    {
        var server = new UsenetServer(1, "A", "news.example.com", 563, true, "u", "p", 10, 0, true, 0);
        Assert.True(StreamZH.Library.Usenet.UsenetServerService.SameDestination(server, server with { Host = "NEWS.example.com.", Connections = 20 }));
        Assert.False(StreamZH.Library.Usenet.UsenetServerService.SameDestination(server, server with { Port = 119 }));
        Assert.False(StreamZH.Library.Usenet.UsenetServerService.SameDestination(server, server with { UseSsl = false }));

        static Dictionary<string, JsonElement> S(object o) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o))!;
        var stored = S(new { host = "qbit.local", port = 8080, useSsl = false, urlBase = "/qbt/" });
        Assert.True(StreamZH.Library.Downloads.Torrent.DownloadClientService.SameDestination(stored, S(new { host = "qbit.local", port = 8080, useSsl = false, urlBase = "/qbt", category = "x" })));
        Assert.False(StreamZH.Library.Downloads.Torrent.DownloadClientService.SameDestination(stored, S(new { host = "qbit.local", port = 8080, useSsl = false, urlBase = "/other" })));
    }

    [Fact]
    public void Tool_parameters_come_from_an_allow_list()
    {
        Assert.Equal(["-t4", "-T2", "-m1024", "-N", "-q"], ToolArguments.Parse("-t4 -T2 -m1024 -N -q", sevenZip: false, out _));
        Assert.Equal(["-mmt4", "-mmt=off", "-bb1", "-bd", "-sccUTF-8", "-mcp=437", "-aou"], ToolArguments.Parse("-mmt4 -mmt=off -bb1 -bd -sccUTF-8 -mcp=437 -aou", sevenZip: true, out _));

        // Allowed by the old deny-list, but they make 7-Zip write or read elsewhere.
        foreach (var dangerous in new[] { "-spf", "-spe", "-snl", "-sccUTF-8x", "-stl", "-ssw" })
        {
            Assert.Null(ToolArguments.Parse(dangerous, sevenZip: true, out var problem));
            Assert.Contains("not allowed", problem, StringComparison.Ordinal);
        }

        Assert.Null(ToolArguments.Parse("-a", sevenZip: false, out _));
        Assert.Null(ToolArguments.Parse("-p", sevenZip: false, out _));
        Assert.False(ToolArguments.IsAllowed("-o/tmp", sevenZip: true));
    }

    [Fact]
    public void Tool_parameters_stored_by_an_earlier_version_are_dropped_when_not_allowed()
    {
        var file = Path.Combine(Path.GetTempPath(), $"streamzh-args-{Guid.NewGuid():N}.db");
        try
        {
            var database = new Database(file);
            database.Migrate();
            var config = new ConfigRepository(database);
            config.Set(ConfigKeys.DownloadSevenZipArguments, "-mmt4\n-spf\n-bb1");
            config.Set(ConfigKeys.DownloadPar2Arguments, "-t2\n-a");
            var settings = new DownloadSettings(config, new StreamZH.Core.AppPaths(Path.GetTempPath()));
            Assert.Equal(["-mmt4", "-bb1"], settings.PostProcess.SevenZipArguments);
            Assert.Equal(["-t2"], settings.PostProcess.Par2Arguments);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData(PostProcessTool.Par2, "par2", true)]
    [InlineData(PostProcessTool.Par2, "par2.exe", true)]
    [InlineData(PostProcessTool.Par2, "par2j64.exe", true)]
    [InlineData(PostProcessTool.Par2, "PAR2.EXE", true)]
    [InlineData(PostProcessTool.Par2, "sh", false)]
    [InlineData(PostProcessTool.Par2, "par2.sh", false)]
    [InlineData(PostProcessTool.SevenZip, "7zz", true)]
    [InlineData(PostProcessTool.SevenZip, "7za.exe", true)]
    [InlineData(PostProcessTool.SevenZip, "cmd.exe", false)]
    [InlineData(PostProcessTool.FFprobe, "ffprobe", true)]
    [InlineData(PostProcessTool.FFprobe, "ffmpeg", false)]
    public void Configured_tools_must_be_named_like_the_tool(PostProcessTool tool, string name, bool allowed)
    {
        Assert.Equal(allowed, PostProcessTools.IsToolFileName(tool, name));
    }

    private static Dictionary<string, JsonElement> Input(object values) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values))!;
}
