// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Downloads;
using StreamZH.Core.Indexers;
using StreamZH.Indexers;
using StreamZH.Indexers.Newznab;
using static StreamZH.Tests.Indexers.NewznabTestSupport;

namespace StreamZH.Tests.Indexers;

/// <summary>The Newznab client speaking Torznab to a fake Jackett/Prowlarr: search, caps, .torrent and magnet downloads.</summary>
public class TorznabClientTests
{
    private const string JackettPath = "http://127.0.0.1:1234/api/v2.0/indexers/exampletracker/results/torznab";

    private static readonly MovieSearchCriteria Matrix = new() { Titles = ["The Matrix"], Year = 1999, TmdbId = 603, ImdbId = "tt0133093" };

    /// <summary>A tiny .torrent of generated data (bencoded, one 5 byte file).</summary>
    public static byte[] TinyTorrent() =>
        [.. "d8:announce38:https://tracker.example.org/announce4:infod6:lengthi5e4:name8:test.bin12:piece lengthi16384e6:pieces20:"u8,
            .. new byte[20], .. "ee"u8];

    private static IndexerDefinition Torznab(string baseUrl = JackettPath) =>
        Indexer(baseUrl) with { Implementation = IndexerDefinition.Torznab };

    private static ReleaseInfo Release(string downloadUrl, string? magnet = null) => new()
    {
        Guid = "g1", Title = "Some.Movie.2020.1080p.BluRay.x264-GRP", DownloadUrl = downloadUrl, MagnetUrl = magnet, Protocol = DownloadProtocol.Torrent,
    };

    private static HttpResponseMessage Torrent() =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(TinyTorrent()) { Headers = { ContentType = new MediaTypeHeaderValue("application/x-bittorrent") } } };

    [Fact]
    public async Task Movie_search_on_a_jackett_indexer_returns_torrent_releases()
    {
        var handler = new StubIndexer { CapsXml = Fixture("torznab_caps_jackett.xml"), Respond = _ => Xml(Fixture("torznab_jackett.xml")) };

        var results = await Client(handler).SearchAsync(Torznab(), Matrix, CancellationToken.None);

        var url = Assert.Single(handler.ApiRequests);
        Assert.Equal("/api/v2.0/indexers/exampletracker/results/torznab/api", url.AbsolutePath);
        Assert.Equal(("movie", "0133093", "2000,2040"), (Query(url)["t"], Query(url)["imdbid"], Query(url)["cat"]));
        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.Equal((DownloadProtocol.Torrent, 7, "TestIndexer", 12), (r.Protocol, r.IndexerId, r.Indexer, r.IndexerPriority)));
        Assert.Equal(57, results[0].Seeders);
    }

    [Fact]
    public async Task Caps_of_jackett_include_its_own_categories_and_wrong_documents_name_torznab()
    {
        var handler = new StubIndexer { CapsXml = Fixture("torznab_caps_jackett.xml") };
        var caps = await Client(handler).GetCapabilitiesAsync(Torznab(), CancellationToken.None);
        Assert.Equal(["q", "imdbid"], caps.MovieSearchParams);
        Assert.Equal([2000, 5000, 100001, 100002], caps.Categories.Select(c => c.Id));
        Assert.Equal([2040, 2045], caps.Categories[0].SubCategories.Select(c => c.Id));

        handler.CapsXml = "<rss><channel /></rss>";
        var error = await Assert.ThrowsAsync<IndexerException>(() => Client(handler).GetCapabilitiesAsync(Torznab(), CancellationToken.None));
        Assert.Contains("did not return Torznab capabilities", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Downloads_the_torrent_file_with_the_api_key_for_the_indexer_host()
    {
        var handler = new StubIndexer { Respond = _ => Torrent() };

        var bytes = await Client(handler).DownloadNzbAsync(Torznab(), Release("http://127.0.0.1:1234/dl/exampletracker/?path=abc"), CancellationToken.None);

        Assert.Equal(TinyTorrent(), bytes);
        Assert.Equal("http://127.0.0.1:1234/dl/exampletracker/?path=abc&apikey=abcd1234secret", Assert.Single(handler.Requests).ToString());
    }

    [Fact]
    public async Task A_magnet_only_release_needs_no_request()
    {
        var handler = new StubIndexer();
        const string Magnet = "magnet:?xt=urn:btih:9c1e8039dd32f16e7d82f60e20b7e522ef490757&dn=x";

        var bytes = await Client(handler).DownloadNzbAsync(Torznab(), Release(Magnet, Magnet), CancellationToken.None);

        Assert.Equal(Magnet, Encoding.UTF8.GetString(bytes));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_redirect_to_a_magnet_link_returns_the_magnet_link()
    {
        const string Magnet = "magnet:?xt=urn:btih:8411900268d72084dc798ada753204d2f6a82c2e";
        var handler = new StubIndexer
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(Magnet) } },
        };

        var bytes = await Client(handler).DownloadNzbAsync(Torznab(), Release("http://127.0.0.1:1234/dl/1"), CancellationToken.None);

        Assert.True(Core.Indexers.TorrentFiles.TryGetMagnet(bytes, out var magnet));
        Assert.Equal(Magnet, magnet);
    }

    [Fact]
    public async Task An_error_page_falls_back_to_the_magnet_link_or_fails_without_one()
    {
        var handler = new StubIndexer { Respond = _ => Xml("<!DOCTYPE html><html><body>Login</body></html>", mediaType: "text/html") };
        const string Magnet = "magnet:?xt=urn:btih:8411900268d72084dc798ada753204d2f6a82c2e";

        var bytes = await Client(handler).DownloadNzbAsync(Torznab(), Release("http://127.0.0.1:1234/dl/1", Magnet), CancellationToken.None);
        Assert.Equal(Magnet, Encoding.UTF8.GetString(bytes));

        var error = await Assert.ThrowsAsync<IndexerException>(() => Client(handler).DownloadNzbAsync(Torznab(), Release("http://127.0.0.1:1234/dl/1"), CancellationToken.None));
        Assert.Contains("HTML page instead of a .torrent file", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_api_key_is_reported_and_not_hidden_by_the_magnet_fallback()
    {
        var handler = new StubIndexer { Respond = _ => Xml("""<error code="100" description="Invalid API Key" />""", mediaType: "application/xml") };

        var error = await Assert.ThrowsAsync<IndexerException>(() =>
            Client(handler).DownloadNzbAsync(Torznab(), Release("http://127.0.0.1:1234/dl/1", "magnet:?xt=urn:btih:abc"), CancellationToken.None));

        Assert.True(error.IsAuthError);
        Assert.DoesNotContain("abcd1234secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Usenet_indexers_still_require_an_nzb()
    {
        var handler = new StubIndexer { Respond = _ => Torrent() };

        await Assert.ThrowsAsync<IndexerException>(() => Client(handler).DownloadNzbAsync(Indexer(), Release("http://127.0.0.1:1234/getnzb/1"), CancellationToken.None));
    }

    /// <summary>
    /// A real HTTP fake Torznab server (HttpListener) and the client as registered by the app: an http → http redirect
    /// is followed, an http → magnet redirect comes back as the magnet link (.NET's own redirect handling fails on it).
    /// </summary>
    [Fact]
    public async Task Real_http_fake_server_with_redirects()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var server = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                var path = context.Request.Url!.AbsolutePath;
                var query = context.Request.Url.Query;
                var response = context.Response;
                if (path.EndsWith("/api", StringComparison.Ordinal) && query.Contains("t=caps", StringComparison.Ordinal))
                {
                    await Write(response, "application/xml", Encoding.UTF8.GetBytes(Fixture("torznab_caps_jackett.xml")));
                }
                else if (path.EndsWith("/api", StringComparison.Ordinal))
                {
                    await Write(response, "application/rss+xml", Encoding.UTF8.GetBytes(Fixture("torznab_prowlarr.xml")));
                }
                else if (path == "/dl/redirect-http")
                {
                    response.StatusCode = 302;
                    response.AddHeader("Location", $"http://127.0.0.1:{port}/dl/file.torrent");
                    response.Close();
                }
                else if (path == "/dl/redirect-magnet")
                {
                    response.StatusCode = 302;
                    response.AddHeader("Location", "magnet:?xt=urn:btih:283d77dd50745e82fd732406b405ca3e4d389ad9&dn=Some.Movie");
                    response.Close();
                }
                else if (path == "/dl/file.torrent")
                {
                    await Write(response, "application/x-bittorrent", TinyTorrent());
                }
                else
                {
                    response.StatusCode = 404;
                    response.Close();
                }
            }
        });

        try
        {
            // The client exactly as the app registers it (AddNewznabIndexers: the handler's own redirects are off).
            using var services = new ServiceCollection().AddLogging().AddNewznabIndexers().BuildServiceProvider();
            var client = services.GetRequiredService<NewznabClient>();
            var indexer = Torznab($"http://127.0.0.1:{port}/5");

            var found = await client.SearchAsync(indexer, Matrix, CancellationToken.None);
            Assert.Equal(140, Assert.Single(found).Seeders);

            Assert.Equal(TinyTorrent(), await client.DownloadNzbAsync(indexer, Release($"http://127.0.0.1:{port}/dl/redirect-http"), CancellationToken.None));

            var magnet = await client.DownloadNzbAsync(indexer, Release($"http://127.0.0.1:{port}/dl/redirect-magnet"), CancellationToken.None);
            Assert.StartsWith("magnet:?xt=urn:btih:283d77dd", Encoding.UTF8.GetString(magnet), StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            await server;
        }
    }

    private static async Task Write(HttpListenerResponse response, string contentType, byte[] body)
    {
        response.ContentType = contentType;
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body);
        response.Close();
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }
}
