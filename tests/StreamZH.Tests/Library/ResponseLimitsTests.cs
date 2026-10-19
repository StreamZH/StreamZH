// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.IO.Compression;
using System.Net;
using System.Text;
using StreamZH.Core.Net;
using StreamZH.Library.ImportLists;
using StreamZH.Library.Notifications;
using StreamZH.Tests.TrashSync;

namespace StreamZH.Tests.Library;

/// <summary>S2-4: answers of servers an admin points StreamZH at are read up to a limit, not into unbounded memory.</summary>
public sealed class ResponseLimitsTests
{
    /// <summary>Streams <paramref name="length"/> bytes of '[' without a Content-Length (chunked), never holding them.</summary>
    private sealed class EndlessContent(long length) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var chunk = Enumerable.Repeat((byte)'[', 64 * 1024).ToArray();
            for (long sent = 0; sent < length; sent += chunk.Length)
            {
                await stream.WriteAsync(chunk);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new ChunkStream(length));
    }

    /// <summary>A read-only stream of '[' bytes.</summary>
    private sealed class ChunkStream(long length) : Stream
    {
        private long _left = length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, _left);
            buffer.AsSpan(offset, n).Fill((byte)'[');
            _left -= n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Answer(Func<HttpContent> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content() });
    }

    [Fact]
    public async Task An_import_list_answer_over_20_mb_is_refused()
    {
        using var http = new HttpClient(new Answer(() => new EndlessContent(ResponseLimits.Json + (1024 * 1024))));
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://lists.example/list.json");

        var error = await Assert.ThrowsAsync<ImportListException>(() => ImportListHttp.SendAsync(http, request, "StreamZH list", CancellationToken.None));

        Assert.Contains("larger than 20 MB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_import_list_answer_with_a_large_content_length_is_refused_before_reading()
    {
        var body = new ByteArrayContent([]);
        body.Headers.ContentLength = ResponseLimits.Json + 1;
        using var http = new HttpClient(new Answer(() => body));
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://lists.example/list.json");

        await Assert.ThrowsAsync<ImportListException>(() => ImportListHttp.SendAsync(http, request, "RSS list", CancellationToken.None));
    }

    [Fact]
    public async Task A_normal_answer_is_read_with_its_charset()
    {
        using var http = new HttpClient(new Answer(() => new StringContent("{\"title\":\"Amélie\"}", Encoding.Latin1, "application/json")));
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://lists.example/list.json");

        Assert.Equal("{\"title\":\"Amélie\"}", await ImportListHttp.SendAsync(http, request, "StreamZH list", CancellationToken.None));
    }

    [Fact]
    public async Task A_connection_answer_over_20_mb_is_refused()
    {
        using var http = new HttpClient(new Answer(() => new EndlessContent(ResponseLimits.Json + (1024 * 1024))));
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://webhook.example/hook");

        var error = await Assert.ThrowsAsync<ConnectException>(() => ConnectHttp.SendAsync(http, request, CancellationToken.None));

        Assert.Contains("larger than 20 MB", error.Message, StringComparison.Ordinal);
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task Copy_stops_at_the_limit()
    {
        using var target = new MemoryStream();
        await ResponseLimits.CopyAsync(new ChunkStream(1000), target, 1000, "x", CancellationToken.None);
        Assert.Equal(1000, target.Length);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ResponseLimits.CopyAsync(new ChunkStream(1001), new MemoryStream(), 1000, "The archive", CancellationToken.None));
        Assert.Contains("The archive is larger than", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => ResponseLimits.Copy(new ChunkStream(1001), new MemoryStream(), 1000, "x"));
    }

    [Fact]
    public async Task A_trash_archive_with_a_huge_file_is_rejected()
    {
        // A zip bomb: metadata.json unpacks to 17 MB of the same byte.
        using var bed = new TrashTestBed();
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("Guides-master/metadata.json").Open();
            ResponseLimits.Copy(new ChunkStream(17 * ResponseLimits.MB), entry, long.MaxValue, "x");
        }

        bed.Http.Archive = memory.ToArray();

        var result = await bed.Store.DownloadAsync(CancellationToken.None);

        Assert.Contains("larger than 16 MB", result.Error, StringComparison.Ordinal);
        Assert.False(bed.Store.HasCopy);
    }
}
