// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Library.Notifications;

namespace StreamZH.Tests.Connect;

public sealed class MediaServerSenderTests
{
    private const string PlexSections = """
        {"MediaContainer":{"size":3,"Directory":[
          {"key":"1","type":"movie","title":"Movies","Location":[{"id":1,"path":"/data/movies"}]},
          {"key":"2","type":"show","title":"TV Shows","Location":[{"id":2,"path":"/data/tv"}]},
          {"key":"3","type":"movie","title":"Kids","Location":[{"id":3,"path":"/data/kids"}]}]}}
        """;

    private readonly FakeHttp _http = new();

    [Fact]
    public async Task Plex_scans_the_mapped_folder_in_the_matching_library_only()
    {
        _http.Respond = r => (HttpStatusCode.OK, r.Uri.AbsolutePath == "/library/sections" ? PlexSections : "{}");
        var context = ConnectTestData.Context("plex", new { host = "plex.local", port = 32400, authToken = "secret-token", mapFrom = "/media", mapTo = "/data" });

        await new PlexSender(_http, NullLogger<PlexSender>.Instance).SendAsync(context, ConnectTestData.MovieImport(), default);

        Assert.Equal(2, _http.Requests.Count);
        var refresh = _http.Last;
        Assert.Equal("/library/sections/1/refresh", refresh.Uri.AbsolutePath);
        Assert.Equal("?path=%2Fdata%2Fmovies%2FDune%20%282021%29", refresh.Uri.Query);
        Assert.Equal("plex.local", refresh.Uri.Host);
        Assert.Equal("http", refresh.Uri.Scheme);
        Assert.Equal("secret-token", refresh.Header("X-Plex-Token"));
        Assert.Equal("application/json", refresh.Header("Accept"));
    }

    [Fact]
    public async Task Plex_scans_all_libraries_of_the_type_when_no_location_matches_and_honours_ssl()
    {
        _http.Respond = r => (HttpStatusCode.OK, r.Uri.AbsolutePath == "/library/sections" ? PlexSections : "{}");
        var context = ConnectTestData.Context("plex", new { host = "plex.local", port = 32443, useSsl = true, authToken = "t" });

        await new PlexSender(_http, NullLogger<PlexSender>.Instance).SendAsync(context, ConnectTestData.MovieImport(folder: "/elsewhere/Dune (2021)"), default);

        var refreshes = _http.Requests.Skip(1).ToList();
        Assert.Equal(["/library/sections/1/refresh", "/library/sections/3/refresh"], refreshes.Select(r => r.Uri.AbsolutePath));
        Assert.All(refreshes, r => Assert.Equal(string.Empty, r.Uri.Query));
        Assert.Equal("https://plex.local:32443/library/sections/1/refresh", refreshes[0].Uri.ToString());
    }

    [Fact]
    public async Task Plex_reports_unauthorized_without_retry_and_does_nothing_with_update_off()
    {
        _http.Respond = _ => (HttpStatusCode.Unauthorized, "<html>401</html>");
        var sender = new PlexSender(_http, NullLogger<PlexSender>.Instance);
        var context = ConnectTestData.Context("plex", new { host = "plex.local", authToken = "wrong" });

        var ex = await Assert.ThrowsAsync<ConnectException>(() => sender.SendAsync(context, ConnectTestData.MovieImport(), default));
        Assert.Equal("unauthorized", ex.Message);
        Assert.False(ex.Retryable);
        await Assert.ThrowsAsync<ConnectException>(() => sender.TestAsync(context, default));

        var off = ConnectTestData.Context("plex", new { host = "plex.local", authToken = "t", updateLibrary = false });
        var before = _http.Requests.Count;
        await sender.SendAsync(off, ConnectTestData.MovieImport(), default);
        Assert.Equal(before, _http.Requests.Count);
    }

    [Fact]
    public async Task Plex_test_counts_the_libraries()
    {
        _http.Respond = _ => (HttpStatusCode.OK, PlexSections);
        var context = ConnectTestData.Context("plex", new { host = "plex.local", authToken = "t" });

        var message = await new PlexSender(_http, NullLogger<PlexSender>.Instance).TestAsync(context, default);

        Assert.Equal("Connected to Plex: 2 movie and 1 TV library.", message);
    }

    [Fact]
    public async Task Jellyfin_reports_the_changed_folder_with_the_token_header()
    {
        var context = ConnectTestData.Context("jellyfin", new { url = "http://jelly:8096/", apiKey = "jf-key", mapFrom = "/media", mapTo = "D:\\Media" });

        await new JellyfinEmbySender("jellyfin", _http).SendAsync(context, ConnectTestData.MovieImport(), default);

        var request = Assert.Single(_http.Requests);
        Assert.Equal("http://jelly:8096/Library/Media/Updated", request.Uri.ToString());
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("Token=\"jf-key\"", request.Header("Authorization"), StringComparison.Ordinal);
        var update = Assert.Single(request.Json.GetProperty("Updates").EnumerateArray());
        Assert.Equal("D:\\Media\\movies\\Dune (2021)", update.GetProperty("Path").GetString());
        Assert.Equal("Modified", update.GetProperty("UpdateType").GetString());
    }

    [Fact]
    public async Task Jellyfin_full_scan_mode_and_grabs_are_ignored()
    {
        var sender = new JellyfinEmbySender("jellyfin", _http);
        var full = ConnectTestData.Context("jellyfin", new { url = "http://jelly:8096", apiKey = "k", updateMode = "full" });

        await sender.SendAsync(full, ConnectTestData.EpisodeGrab(), default);
        Assert.Empty(_http.Requests);

        await sender.SendAsync(full, ConnectTestData.MovieImport(), default);
        Assert.Equal("/Library/Refresh", Assert.Single(_http.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task Emby_sends_admin_notifications_and_tests_the_server()
    {
        _http.Respond = r => (HttpStatusCode.OK, r.Uri.AbsolutePath == "/System/Info" ? """{"ServerName":"Den","Version":"4.9.1"}""" : string.Empty);
        var sender = new JellyfinEmbySender("emby", _http);
        var context = ConnectTestData.Context("emby", new { url = "http://emby:8096", apiKey = "emby-key", sendNotifications = true });

        await sender.SendAsync(context, ConnectTestData.MovieImport(), default);

        Assert.Equal(["/Notifications/Admin", "/Library/Media/Updated"], _http.Requests.Select(r => r.Uri.AbsolutePath));
        Assert.All(_http.Requests, r => Assert.Equal("emby-key", r.Header("X-Emby-Token")));
        var notification = _http.Requests[0].Json;
        Assert.Equal("Movie Downloaded", notification.GetProperty("Name").GetString());
        Assert.Equal("Dune (2021) · WEBDL-1080p · FLUX", notification.GetProperty("Description").GetString());
        Assert.Equal("https://image.tmdb.org/t/p/w500/dune.jpg", notification.GetProperty("ImageUrl").GetString());

        Assert.Equal("Connected to Den 4.9.1.", await sender.TestAsync(context, default));
    }

    [Fact]
    public async Task Kodi_notifies_scans_the_folder_and_cleans()
    {
        _http.Respond = r => (HttpStatusCode.OK, r.Json.GetProperty("method").GetString() == "Player.GetActivePlayers"
            ? """{"jsonrpc":"2.0","id":1,"result":[{"playerid":0,"type":"audio"}]}"""
            : """{"jsonrpc":"2.0","id":1,"result":"OK"}""");
        var context = ConnectTestData.Context("kodi", new { host = "kodi.local", username = "kodi", password = "pw", cleanLibrary = true, displayTime = 8, mapFrom = "/media", mapTo = "smb://nas/media" });

        await new KodiSender(_http, NullLogger<KodiSender>.Instance).SendAsync(context, ConnectTestData.MovieImport(), default);

        Assert.Equal(
            ["GUI.ShowNotification", "Player.GetActivePlayers", "VideoLibrary.Scan", "VideoLibrary.Clean"],
            _http.Requests.Select(r => r.Json.GetProperty("method").GetString()));
        Assert.All(_http.Requests, r => Assert.Equal("http://kodi.local:8080/jsonrpc", r.Uri.ToString()));
        Assert.Equal("Basic " + Convert.ToBase64String("kodi:pw"u8.ToArray()), _http.Requests[0].Header("Authorization"));
        var notification = _http.Requests[0].Json.GetProperty("params");
        Assert.Equal("StreamZH - Movie Downloaded", notification.GetProperty("title").GetString());
        Assert.Equal(8000, notification.GetProperty("displaytime").GetInt32());
        Assert.Equal("smb://nas/media/movies/Dune (2021)/", _http.Requests[2].Json.GetProperty("params").GetProperty("directory").GetString());
    }

    [Fact]
    public async Task Kodi_skips_the_update_while_a_video_plays_unless_always_update()
    {
        _http.Respond = r => (HttpStatusCode.OK, r.Json.GetProperty("method").GetString() == "Player.GetActivePlayers"
            ? """{"result":[{"playerid":1,"type":"video"}]}"""
            : """{"result":"OK"}""");
        var sender = new KodiSender(_http, NullLogger<KodiSender>.Instance);

        await sender.SendAsync(ConnectTestData.Context("kodi", new { host = "kodi", notify = false }), ConnectTestData.MovieImport(), default);
        Assert.Equal("Player.GetActivePlayers", Assert.Single(_http.Requests).Json.GetProperty("method").GetString());

        await sender.SendAsync(ConnectTestData.Context("kodi", new { host = "kodi", notify = false, alwaysUpdate = true }), ConnectTestData.MovieImport(), default);
        Assert.Equal("VideoLibrary.Scan", _http.Last.Json.GetProperty("method").GetString());
    }

    [Fact]
    public async Task Kodi_reports_json_rpc_errors()
    {
        _http.Respond = _ => (HttpStatusCode.OK, """{"error":{"code":-32601,"message":"Method not found."}}""");
        var ex = await Assert.ThrowsAsync<ConnectException>(() =>
            new KodiSender(_http, NullLogger<KodiSender>.Instance).TestAsync(ConnectTestData.Context("kodi", new { host = "kodi" }), default));

        Assert.Equal("rejected: Method not found. (JSONRPC.Version)", ex.Message);
    }

    [Fact]
    public async Task Network_errors_and_server_errors_are_retryable()
    {
        _http.Respond = _ => (HttpStatusCode.BadGateway, "{}");
        var ex = await Assert.ThrowsAsync<ConnectException>(() =>
            new JellyfinEmbySender("jellyfin", _http).SendAsync(ConnectTestData.Context("jellyfin", new { url = "http://j", apiKey = "k" }), ConnectTestData.MovieImport(), default));
        Assert.True(ex.Retryable);
        Assert.Equal("server error 502", ex.Message);

        using var failing = new FailingHandler();
        var unreachable = await Assert.ThrowsAsync<ConnectException>(() =>
            new PlexSender(failing, NullLogger<PlexSender>.Instance).TestAsync(ConnectTestData.Context("plex", new { host = "plex", authToken = "t" }), default));
        Assert.True(unreachable.Retryable);
        Assert.StartsWith("not reachable", unreachable.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/media/movies/Dune", "/media", "/data", "/data/movies/Dune")]
    [InlineData("/media/movies/Dune", "/media/", "/data/", "/data/movies/Dune")]
    [InlineData("/media/movies/Dune", "/other", "/data", "/media/movies/Dune")]
    [InlineData("/media/movies/Dune", "", "/data", "/media/movies/Dune")]
    [InlineData("/media/movies/Dune", "/media", "D:\\Media", "D:\\Media\\movies\\Dune")]
    [InlineData("D:\\Media\\Movies\\Dune", "D:\\Media", "/mnt/media", "/mnt/media/Movies/Dune")]
    [InlineData("/media", "/media", "/data", "/data")]
    [InlineData("/mediathek/x", "/media", "/data", "/mediathek/x")]
    public void Path_mapping(string path, string from, string to, string expected) =>
        Assert.Equal(expected, ConnectPaths.Map(path, from, to));

    private sealed class FailingHandler : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused");
    }
}
