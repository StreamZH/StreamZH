// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using StreamZH.Core.Indexers;
using StreamZH.Core.Net;
using StreamZH.Indexers.Newznab;
using static StreamZH.Tests.Indexers.NewznabTestSupport;

namespace StreamZH.Tests.Indexers;

/// <summary>
/// S2-3: links in indexer feeds and redirects may not reach this computer, link-local or cloud metadata addresses
/// unless they stay on the indexer's own host; since 0.12 (V-10) not the LAN either, except configured indexers and download clients.
/// </summary>
public class OutboundAddressGuardTests
{
    private const string IndexerUrl = "http://indexer.test/";

    private static readonly OutboundAddressGuard Rebinding = new((host, _) => Task.FromResult(host switch
    {
        "rebind.test" => new[] { IPAddress.Loopback },
        "metadata.test" => [IPAddress.Parse("169.254.169.254")],
        "mixed.test" => [IPAddress.Parse("203.0.113.7"), IPAddress.Loopback],
        _ => [IPAddress.Parse("203.0.113.7")],
    }));

    private static ReleaseInfo Release(string downloadUrl, string? magnet = null) => new()
    {
        Guid = "g1",
        Title = "Some.Movie.2020.1080p.BluRay.x264-GRP",
        DownloadUrl = downloadUrl,
        MagnetUrl = magnet,
    };

    private static HttpResponseMessage Nzb()
    {
        var content = new ByteArrayContent(FixtureBytes("sample.nzb"));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-nzb");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } };

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.1.2.3", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("169.254.10.1", false)]
    [InlineData("100.100.100.200", false)]
    [InlineData("168.63.129.16", false)]
    [InlineData("224.0.0.251", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00:ec2::254", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:169.254.169.254", false)]
    [InlineData("64:ff9b::7f00:1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.4.4", true)]
    [InlineData("100.64.1.1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("203.0.113.7", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("::ffff:192.168.1.20", true)]
    public void Classifies_addresses(string address, bool allowed) =>
        Assert.Equal(allowed, OutboundAddressGuard.IsAllowed(IPAddress.Parse(address)));

    [Theory]
    [InlineData("http://127.0.0.1:8080/admin")]
    [InlineData("http://169.254.169.254/latest/meta-data/iam/security-credentials/")]
    [InlineData("http://[::1]:9797/api/v1/system/backup")]
    [InlineData("http://localhost:9797/api")]
    public async Task A_feed_link_to_another_host_may_not_reach_loopback_or_metadata(string link)
    {
        var handler = new StubIndexer { Respond = _ => Nzb() };

        var error = await Assert.ThrowsAsync<IndexerException>(() =>
            Client(handler).DownloadNzbAsync(Indexer(IndexerUrl), Release(link), CancellationToken.None));

        Assert.Contains("may not go there", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("http://127.0.0.1/secret")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    public async Task A_redirect_from_the_indexer_may_not_reach_loopback_or_metadata(string target)
    {
        var handler = new StubIndexer
        {
            Respond = r => r.RequestUri!.Host == "indexer.test" ? Redirect(target) : Nzb(),
        };

        await Assert.ThrowsAsync<IndexerException>(() =>
            Client(handler).DownloadNzbAsync(Indexer(IndexerUrl), Release("http://indexer.test/getnzb/abc.nzb"), CancellationToken.None));

        Assert.Equal(["indexer.test"], handler.Requests.Select(u => u.Host));
    }

    [Fact]
    public async Task A_name_that_resolves_to_loopback_is_blocked()
    {
        var handler = new StubIndexer { Respond = r => r.RequestUri!.Host == "indexer.test" ? Redirect("http://rebind.test/x.nzb") : Nzb() };

        await Assert.ThrowsAsync<IndexerException>(() =>
            Client(handler, guard: Rebinding).DownloadNzbAsync(Indexer(IndexerUrl), Release("http://indexer.test/getnzb/abc.nzb"), CancellationToken.None));
        await Assert.ThrowsAsync<IndexerException>(() =>
            Client(handler, guard: Rebinding).DownloadNzbAsync(Indexer(IndexerUrl), Release("http://mixed.test/x.nzb"), CancellationToken.None));
        Assert.DoesNotContain(handler.Requests, u => u.Host is "rebind.test" or "mixed.test");
    }

    /// <summary>
    /// V-10: a link or redirect to another host than the indexer may not reach the local network either (a blind GET to
    /// the router or NAS admin page), unless that host is another configured indexer or download client.
    /// </summary>
    [Fact]
    public async Task Lan_addresses_only_for_configured_services_and_public_hosts_stay_allowed()
    {
        var handler = new StubIndexer { Respond = r => r.RequestUri!.Host == "indexer.test" ? Redirect("http://192.168.1.20:9117/dl/abc.nzb") : Nzb() };
        var client = Client(handler, guard: Rebinding);

        await Assert.ThrowsAsync<IndexerException>(() => client.DownloadNzbAsync(Indexer(IndexerUrl), Release("http://indexer.test/getnzb/abc.nzb"), CancellationToken.None));
        await Assert.ThrowsAsync<IndexerException>(() => client.DownloadNzbAsync(Indexer(IndexerUrl), Release("http://10.0.0.1/admin"), CancellationToken.None));
        await Assert.ThrowsAsync<IndexerException>(() => client.DownloadNzbAsync(Indexer(IndexerUrl), Release("http://[fd00::1]/admin"), CancellationToken.None));
        await client.DownloadNzbAsync(Indexer(IndexerUrl), Release("https://cdn.example/abc.nzb"), CancellationToken.None);
        Assert.Equal(["indexer.test", "cdn.example"], handler.Requests.Select(u => u.Host));

        // Jackett on 192.168.1.20 is a configured indexer: its links are fine.
        var configured = new NewznabClient(new HttpClient(handler), new NewznabCapabilitiesCache(), guard: Rebinding, configuredHosts: new Hosts("192.168.1.20"));
        await configured.DownloadNzbAsync(Indexer(IndexerUrl), Release("http://indexer.test/getnzb/abc.nzb"), CancellationToken.None);
        Assert.Contains(handler.Requests, u => u.Host == "192.168.1.20");
    }

    [Theory]
    [InlineData("192.168.1.20", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("172.16.4.4", false)]
    [InlineData("172.31.255.1", false)]
    [InlineData("100.64.1.1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("::ffff:192.168.1.20", false)]
    [InlineData("64:ff9b::a00:1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("203.0.113.7", true)]
    [InlineData("2001:db8::1", true)]
    public void Restricted_requests_reach_only_public_addresses(string address, bool reachable) =>
        Assert.Equal(reachable, OutboundAddressGuard.IsPublic(IPAddress.Parse(address)));

    /// <summary>V-10: through an outgoing proxy, a name StreamZH cannot resolve itself is refused (the proxy could resolve it to anything).</summary>
    [Fact]
    public async Task A_name_that_does_not_resolve_is_refused_when_a_proxy_carries_the_request()
    {
        var unresolvable = new OutboundAddressGuard((_, _) => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)), usesProxy: _ => true);
        using var viaProxy = new HttpRequestMessage(HttpMethod.Get, "http://router.internal/admin");
        OutboundAddressGuard.Restrict(viaProxy, "indexer.test");
        await Assert.ThrowsAsync<BlockedDestinationException>(() => unresolvable.CheckAsync(viaProxy, CancellationToken.None));

        var direct = new OutboundAddressGuard((_, _) => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)));
        await direct.CheckAsync(viaProxy, CancellationToken.None);
    }

    private sealed class Hosts(params string[] hosts) : IConfiguredServiceHosts
    {
        public bool Contains(string host) => hosts.Contains(host, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_indexer_on_this_computer_may_link_to_itself()
    {
        // Prowlarr or Jackett on the same machine: the configured host is trusted, also when it is loopback.
        var handler = new StubIndexer { Respond = r => r.RequestUri!.AbsolutePath == "/dl" ? Redirect("http://localhost:9696/file.nzb") : Nzb() };

        await Client(handler).DownloadNzbAsync(Indexer("http://localhost:9696/"), Release("http://localhost:9696/dl"), CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_torznab_torrent_link_to_metadata_is_blocked_and_the_magnet_link_is_used()
    {
        const string magnet = "magnet:?xt=urn:btih:283d77dd50745e82fd732406b405ca3e4d389ad9&dn=Some.Movie";
        var handler = new StubIndexer { Respond = _ => throw new InvalidOperationException("must not be requested") };
        var indexer = Indexer(IndexerUrl) with { Implementation = IndexerDefinition.Torznab };

        var withMagnet = await Client(handler).DownloadNzbAsync(indexer, Release("http://169.254.169.254/latest/meta-data/", magnet), CancellationToken.None);
        await Assert.ThrowsAsync<IndexerException>(() =>
            Client(handler).DownloadNzbAsync(indexer, Release("http://127.0.0.1:8080/x.torrent"), CancellationToken.None));

        Assert.Equal(magnet, Encoding.UTF8.GetString(withMagnet));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_connect_callback_checks_the_address_it_connects_to()
    {
        // DNS rebinding: the name passes the first lookup but points to loopback when the socket connects. The connect
        // callback resolves again and refuses; without the restriction the same name connects.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = ServeAsync(listener);
        using var handler = Rebinding.Apply(new SocketsHttpHandler { UseProxy = false });
        using var http = new HttpClient(handler);

        using var restricted = new HttpRequestMessage(HttpMethod.Get, $"http://rebind.test:{port}/");
        OutboundAddressGuard.Restrict(restricted, "indexer.test");
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => http.SendAsync(restricted));
        Assert.True(error is BlockedDestinationException || error.InnerException is BlockedDestinationException, error.ToString());

        using var trusted = new HttpRequestMessage(HttpMethod.Get, $"http://rebind.test:{port}/");
        OutboundAddressGuard.Restrict(trusted, "rebind.test");
        using var response = await http.SendAsync(trusted);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());

        listener.Stop();
        await server;
    }

    private static async Task ServeAsync(TcpListener listener)
    {
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var buffer = new byte[4096];
                _ = await stream.ReadAsync(buffer);
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray());
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // Listener stopped.
        }
    }
}
