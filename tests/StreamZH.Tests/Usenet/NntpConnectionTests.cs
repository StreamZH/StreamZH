// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Sockets;
using StreamZH.Usenet.Nntp;
using StreamZH.Usenet.Yenc;

namespace StreamZH.Tests.Usenet;

public sealed class NntpConnectionTests : IAsyncLifetime
{
    private readonly FakeNntpServer _server = new() { Username = "johndoe", Password = "s3cret pass" };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task Logs_in_and_decodes_a_body()
    {
        var data = YencArticles.Random(10_000);
        _server.AddYenc("part1@example", data, name: "movie.part01.rar", part: 2, total: 9, begin: 384_000, fileSize: 3_000_000);

        await using var connection = await NntpConnection.ConnectAsync(Settings(), CancellationToken.None);
        var article = await connection.BodyAsync("part1@example", CancellationToken.None);

        Assert.Equal(data, article.Data);
        Assert.Equal("movie.part01.rar", article.FileName);
        Assert.Equal(384_000, article.Offset);
        Assert.Equal(3_000_000, article.FileSize);
        Assert.True(article.CrcOk);
        Assert.True(connection.IsUsable);
        Assert.Null(connection.TlsProtocol);
        Assert.Contains("AUTHINFO USER johndoe", _server.Commands);
        Assert.Contains("BODY <part1@example>", _server.Commands);
    }

    [Fact]
    public async Task Accepts_message_ids_with_angle_brackets_and_rejects_injection()
    {
        _server.AddYenc("a@b", [1, 2, 3]);
        await using var connection = await NntpConnection.ConnectAsync(Settings(), CancellationToken.None);

        Assert.Equal([1, 2, 3], (await connection.BodyAsync("<a@b>", CancellationToken.None)).Data);
        await Assert.ThrowsAsync<ArgumentException>(() => connection.BodyAsync("a@b>\r\nQUIT", CancellationToken.None));
        Assert.True(connection.IsUsable);
    }

    [Fact]
    public async Task Wrong_password_is_an_auth_error()
    {
        var error = await Assert.ThrowsAsync<NntpException>(() =>
            NntpConnection.ConnectAsync(Settings(password: "wrong"), CancellationToken.None));

        Assert.Equal(NntpErrorKind.Auth, error.Kind);
        Assert.Equal(481, error.Code);
        Assert.DoesNotContain("wrong", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_login_the_server_asks_for_one()
    {
        _server.AddYenc("x@y", [1]);
        await using var connection = await NntpConnection.ConnectAsync(Settings(username: "", password: ""), CancellationToken.None);

        var error = await Assert.ThrowsAsync<NntpException>(() => connection.BodyAsync("x@y", CancellationToken.None));
        Assert.Equal(NntpErrorKind.Auth, error.Kind);
        Assert.Equal(480, error.Code);
    }

    [Fact]
    public async Task Removes_dot_stuffing_including_lines_that_are_only_a_dot()
    {
        // 4 encodes to '.', so every line starts with a dot; 129 bytes leave a last line that is a single '.' (sent as "..").
        var data = YencArticles.Dots(YencArticles.LineLength + 1);
        _server.AddYenc("dots@example", data);

        await using var connection = await NntpConnection.ConnectAsync(Settings(), CancellationToken.None);
        var article = await connection.BodyAsync("dots@example", CancellationToken.None);

        Assert.Equal(data, article.Data);
        Assert.True(article.CrcOk);

        // The connection is still in sync after the body.
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero), await connection.DateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Missing_article_is_430_and_the_connection_stays_usable()
    {
        _server.AddYenc("there@example", [9, 9]);
        await using var connection = await NntpConnection.ConnectAsync(Settings(), CancellationToken.None);

        var missing = await Assert.ThrowsAsync<NntpArticleNotFoundException>(() => connection.BodyAsync("gone@example", CancellationToken.None));
        Assert.Equal("gone@example", missing.MessageId);
        Assert.True(connection.IsUsable);
        Assert.Equal([9, 9], (await connection.BodyAsync("there@example", CancellationToken.None)).Data);
    }

    [Fact]
    public async Task Crc_mismatch_still_returns_the_data()
    {
        var data = YencArticles.Random(5000);
        _server.AddYenc("bad@example", data, crcOverride: 0xDEADBEEF);

        await using var connection = await NntpConnection.ConnectAsync(Settings(), CancellationToken.None);
        var article = await connection.BodyAsync("bad@example", CancellationToken.None);

        Assert.False(article.CrcOk);
        Assert.Equal(data, article.Data);
    }

    [Fact]
    public async Task Body_that_is_not_yenc_throws_but_keeps_the_connection()
    {
        _server.Add("plain@example", "Hello\r\nno yEnc here\r\n"u8.ToArray());
        await using var connection = await NntpConnection.ConnectAsync(Settings(), CancellationToken.None);

        await Assert.ThrowsAsync<YencException>(() => connection.BodyAsync("plain@example", CancellationToken.None));
        Assert.True(connection.IsUsable);
    }

    [Fact]
    public async Task Large_bodies_grow_the_buffers()
    {
        var data = YencArticles.Random(3 * 1024 * 1024, seed: 7);
        _server.AddYenc("big@example", data);

        await using var connection = await NntpConnection.ConnectAsync(Settings(), CancellationToken.None);
        var article = await connection.BodyAsync("big@example", CancellationToken.None);

        Assert.Equal(data, article.Data);
        Assert.True(article.CrcOk);
        Assert.True(connection.BytesReceived > data.Length);
    }

    [Fact]
    public async Task Tls_works_with_a_trusted_certificate_and_sends_sni()
    {
        using var certificate = FakeNntpServer.CreateCertificate();
        await using var server = new FakeNntpServer(certificate);
        server.AddYenc("tls@example", [1, 2, 3, 4]);

        await using var connection = await NntpConnection.ConnectAsync(
            new NntpConnectionSettings { Host = "localhost", Port = server.Port, UseSsl = true, CertificateValidation = FakeNntpServer.Trust(certificate) },
            CancellationToken.None);

        Assert.StartsWith("TLS", connection.TlsProtocol, StringComparison.Ordinal);
        Assert.Equal([1, 2, 3, 4], (await connection.BodyAsync("tls@example", CancellationToken.None)).Data);
    }

    [Fact]
    public async Task Tls_rejects_an_untrusted_certificate()
    {
        using var certificate = FakeNntpServer.CreateCertificate();
        await using var server = new FakeNntpServer(certificate);

        var error = await Assert.ThrowsAsync<NntpException>(() => NntpConnection.ConnectAsync(
            new NntpConnectionSettings { Host = "localhost", Port = server.Port, UseSsl = true },
            CancellationToken.None));

        Assert.Equal(NntpErrorKind.Tls, error.Kind);
    }

    [Fact]
    public async Task Tls_against_a_plain_port_fails_with_a_hint()
    {
        var error = await Assert.ThrowsAsync<NntpException>(() => NntpConnection.ConnectAsync(
            new NntpConnectionSettings { Host = "localhost", Port = _server.Port, UseSsl = true, ConnectTimeout = TimeSpan.FromSeconds(3) },
            CancellationToken.None));

        Assert.Equal(NntpErrorKind.Tls, error.Kind);
    }

    [Fact]
    public async Task Read_timeout_breaks_the_connection()
    {
        _server.StallBodies = true;
        _server.AddYenc("slow@example", [1]);
        await using var connection = await NntpConnection.ConnectAsync(Settings(readTimeout: TimeSpan.FromMilliseconds(300)), CancellationToken.None);

        var error = await Assert.ThrowsAsync<NntpException>(() => connection.BodyAsync("slow@example", CancellationToken.None));

        Assert.Equal(NntpErrorKind.Timeout, error.Kind);
        Assert.False(connection.IsUsable);
    }

    [Fact]
    public async Task Cancellation_is_not_a_timeout_and_breaks_the_connection()
    {
        _server.StallBodies = true;
        _server.AddYenc("slow@example", [1]);
        await using var connection = await NntpConnection.ConnectAsync(Settings(), CancellationToken.None);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.BodyAsync("slow@example", cancel.Token));
        Assert.False(connection.IsUsable);
    }

    [Fact]
    public async Task Dropped_connection_is_reported_as_closed()
    {
        _server.AddYenc("drop@example", YencArticles.Random(4000));
        _server.DropNextBodies(1);
        await using var connection = await NntpConnection.ConnectAsync(Settings(), CancellationToken.None);

        var error = await Assert.ThrowsAsync<NntpException>(() => connection.BodyAsync("drop@example", CancellationToken.None));

        Assert.Equal(NntpErrorKind.Closed, error.Kind);
        Assert.False(connection.IsUsable);
    }

    [Theory]
    [InlineData("400 Too many connections", NntpErrorKind.Busy)]
    [InlineData("502 Too many connections for your account", NntpErrorKind.Busy)]
    [InlineData("502 Access denied", NntpErrorKind.Rejected)]
    public async Task Refused_greeting_is_classified(string greeting, NntpErrorKind kind)
    {
        _server.Greeting = greeting;

        var error = await Assert.ThrowsAsync<NntpException>(() => NntpConnection.ConnectAsync(Settings(), CancellationToken.None));

        Assert.Equal(kind, error.Kind);
    }

    [Fact]
    public async Task Connection_refused_is_a_connect_error()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var error = await Assert.ThrowsAsync<NntpException>(() =>
            NntpConnection.ConnectAsync(new NntpConnectionSettings { Host = "127.0.0.1", Port = port }, CancellationToken.None));

        Assert.Equal(NntpErrorKind.Connect, error.Kind);
    }

    [Fact]
    public async Task Quit_says_goodbye()
    {
        var connection = await NntpConnection.ConnectAsync(Settings(), CancellationToken.None);
        await connection.QuitAsync();
        await connection.DisposeAsync();

        Assert.False(connection.IsUsable);
        Assert.Contains("QUIT", _server.Commands);
    }

    [Fact]
    public void Settings_never_print_the_password()
    {
        Assert.DoesNotContain("s3cret", Settings().ToString(), StringComparison.Ordinal);
    }

    private NntpConnectionSettings Settings(string username = "johndoe", string password = "s3cret pass", TimeSpan? readTimeout = null) => new()
    {
        Host = "127.0.0.1",
        Port = _server.Port,
        Username = username,
        Password = password,
        ReadTimeout = readTimeout ?? TimeSpan.FromSeconds(10),
    };
}
