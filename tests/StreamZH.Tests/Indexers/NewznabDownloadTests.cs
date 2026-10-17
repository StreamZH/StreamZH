// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Indexers;
using StreamZH.Indexers;
using StreamZH.Indexers.Newznab;
using static StreamZH.Tests.Indexers.NewznabTestSupport;

namespace StreamZH.Tests.Indexers;

public class NewznabDownloadTests
{
    private static ReleaseInfo Release(string downloadUrl) => new()
    {
        Guid = "g1",
        Title = "Some.Movie.2020.1080p.BluRay.x264-GRP",
        DownloadUrl = downloadUrl,
    };

    private static HttpResponseMessage Bytes(byte[] body, string mediaType = "application/x-nzb", HttpStatusCode status = HttpStatusCode.OK)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(status) { Content = content };
    }

    [Fact]
    public async Task Downloads_and_validates_an_nzb()
    {
        var nzb = FixtureBytes("sample.nzb");
        var handler = new StubIndexer { Respond = _ => Bytes(nzb) };

        var result = await Client(handler).DownloadNzbAsync(Indexer(), Release("http://127.0.0.1:1234/getnzb/abc.nzb&i=1&r=abcd1234secret"), CancellationToken.None);

        Assert.Equal(nzb, result);
        Assert.Equal("http://127.0.0.1:1234/getnzb/abc.nzb&i=1&r=abcd1234secret", Assert.Single(handler.Requests).ToString());
    }

    [Fact]
    public async Task Adds_the_api_key_only_on_the_indexer_host_when_missing()
    {
        var handler = new StubIndexer { Respond = _ => Bytes(FixtureBytes("sample.nzb")) };
        var client = Client(handler);

        await client.DownloadNzbAsync(Indexer(), Release("http://127.0.0.1:1234/api?t=get&id=abc"), CancellationToken.None);
        await client.DownloadNzbAsync(Indexer(), Release("http://127.0.0.1:1234/getnzb/abc.nzb"), CancellationToken.None);
        await client.DownloadNzbAsync(Indexer(), Release("http://127.0.0.1:1234/api?t=get&id=abc&apikey=other"), CancellationToken.None);
        await client.DownloadNzbAsync(Indexer(), Release("https://cdn.example/abc.nzb"), CancellationToken.None);
        await client.DownloadNzbAsync(Indexer(), Release("/getnzb/relative.nzb"), CancellationToken.None);

        Assert.Equal(
            [
                "http://127.0.0.1:1234/api?t=get&id=abc&apikey=abcd1234secret",
                "http://127.0.0.1:1234/getnzb/abc.nzb?apikey=abcd1234secret",
                "http://127.0.0.1:1234/api?t=get&id=abc&apikey=other",
                "https://cdn.example/abc.nzb",
                "http://127.0.0.1:1234/getnzb/relative.nzb?apikey=abcd1234secret",
            ],
            handler.Requests.Select(u => u.ToString()));
    }

    [Fact]
    public async Task Unpacks_gzipped_nzb_files()
    {
        var nzb = FixtureBytes("sample.nzb");
        using var packed = new MemoryStream();
        await using (var gzip = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            await gzip.WriteAsync(nzb);
        }

        var handler = new StubIndexer { Respond = _ => Bytes(packed.ToArray(), "application/x-gzip") };

        var result = await Client(handler).DownloadNzbAsync(Indexer(), Release("http://127.0.0.1:1234/getnzb/abc.nzb"), CancellationToken.None);

        Assert.Equal(nzb, result);
    }

    [Fact]
    public async Task Newznab_error_instead_of_nzb_is_reported_with_its_code()
    {
        var handler = new StubIndexer { Respond = _ => Xml("""<?xml version="1.0"?><error code="501" description="Download limit reached"/>""", mediaType: "text/xml") };

        var ex = await Assert.ThrowsAsync<IndexerException>(() =>
            Client(handler).DownloadNzbAsync(Indexer(), Release("http://127.0.0.1:1234/getnzb/abc.nzb"), CancellationToken.None));

        Assert.Equal(501, ex.Code);
        Assert.Equal(TimeSpan.FromHours(1), ex.RetryAfter);
    }

    [Fact]
    public async Task Rejects_html_and_other_non_nzb_answers()
    {
        var html = new StubIndexer { Respond = _ => Xml("<html><body>Please log in</body></html>", mediaType: "text/html") };
        var htmlEx = await Assert.ThrowsAsync<IndexerException>(() =>
            Client(html).DownloadNzbAsync(Indexer(), Release("http://127.0.0.1:1234/getnzb/abc.nzb"), CancellationToken.None));
        Assert.Contains("HTML page", htmlEx.Message, StringComparison.Ordinal);

        var rss = new StubIndexer { Respond = _ => Xml(Feed(1)) };
        var rssEx = await Assert.ThrowsAsync<IndexerException>(() =>
            Client(rss).DownloadNzbAsync(Indexer(), Release("http://127.0.0.1:1234/getnzb/abc.nzb"), CancellationToken.None));
        Assert.Contains("did not return an NZB", rssEx.Message, StringComparison.Ordinal);

        var binary = new StubIndexer { Respond = _ => Bytes([1, 2, 3, 4], "application/octet-stream") };
        await Assert.ThrowsAsync<IndexerException>(() =>
            Client(binary).DownloadNzbAsync(Indexer(), Release("http://127.0.0.1:1234/getnzb/abc.nzb"), CancellationToken.None));
    }

    [Fact]
    public async Task Http_errors_and_bad_links_are_indexer_errors_without_the_key()
    {
        var handler = new StubIndexer { Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };
        var notFound = await Assert.ThrowsAsync<IndexerException>(() =>
            Client(handler).DownloadNzbAsync(Indexer(), Release("http://127.0.0.1:1234/getnzb/abc.nzb&r=abcd1234secret"), CancellationToken.None));
        Assert.Contains("404", notFound.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, notFound.ToString(), StringComparison.Ordinal);

        var badLink = await Assert.ThrowsAsync<IndexerException>(() =>
            Client(handler).DownloadNzbAsync(Indexer(), Release("magnet:?xt=urn:btih:abc"), CancellationToken.None));
        Assert.Contains("no valid download link", badLink.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refuses_oversized_downloads()
    {
        var handler = new StubIndexer
        {
            Respond = _ =>
            {
                var response = Bytes(FixtureBytes("sample.nzb"));
                response.Content.Headers.ContentLength = NewznabClient.MaxNzbBytes + 1;
                return response;
            },
        };

        var ex = await Assert.ThrowsAsync<IndexerException>(() =>
            Client(handler).DownloadNzbAsync(Indexer(), Release("http://127.0.0.1:1234/getnzb/abc.nzb"), CancellationToken.None));

        Assert.Contains("more than 50 MB", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refuses_oversized_streams_without_content_length()
    {
        var handler = new StubIndexer { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndlessStream()) } };

        var ex = await Assert.ThrowsAsync<IndexerException>(() =>
            Client(handler).DownloadNzbAsync(Indexer(), Release("http://127.0.0.1:1234/getnzb/abc.nzb"), CancellationToken.None));

        Assert.Contains("more than 50 MB", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registers_a_typed_client_with_timeout_and_user_agent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNewznabIndexers(client => client.DefaultRequestHeaders.Add("X-Test", "1"));
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IIndexerClient>();
        Assert.IsType<NewznabClient>(client);
        Assert.Same(provider.GetRequiredService<NewznabCapabilitiesCache>(), provider.GetRequiredService<NewznabCapabilitiesCache>());

        var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IIndexerClient));
        Assert.Equal(TimeSpan.FromSeconds(60), http.Timeout);
        Assert.StartsWith("StreamZH/", http.DefaultRequestHeaders.UserAgent.ToString(), StringComparison.Ordinal);
        Assert.True(http.DefaultRequestHeaders.Contains("X-Test"));
    }

    /// <summary>A stream that never ends and has no length.</summary>
    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'x', offset, count);
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
